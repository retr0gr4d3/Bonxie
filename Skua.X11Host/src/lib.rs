use std::os::fd::{AsRawFd, RawFd};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use x11rb::connection::Connection;
use x11rb::protocol::xproto::{
    Allow, AtomEnum, ButtonIndex, ChangeWindowAttributesAux, ConfigureWindowAux,
    ConnectionExt as XprotoExt, EventMask, GrabMode, InputFocus, ModMask, Window,
};
use x11rb::protocol::Event;
use x11rb::rust_connection::RustConnection;

struct SharedState {
    stop: AtomicBool,
    /// 0 = pending, 1 = attached, -1 = failed.
    attach_state: AtomicI32,
    /// Packed `(width << 32) | height`, 0 when no resize is pending.
    pending_resize: AtomicU64,
    wake_fd: RawFd,
    click_cb: Mutex<Option<(extern "C" fn(usize), usize)>>,
}

impl SharedState {
    fn wake(&self) {
        if self.wake_fd >= 0 {
            let val: u64 = 1;
            unsafe {
                libc::write(self.wake_fd, &val as *const u64 as *const libc::c_void, 8);
            }
        }
    }
}

impl Drop for SharedState {
    fn drop(&mut self) {
        if self.wake_fd >= 0 {
            unsafe {
                libc::close(self.wake_fd);
            }
            self.wake_fd = -1;
        }
    }
}

pub struct BonxieX11Host {
    shared: Arc<SharedState>,
    worker: Option<JoinHandle<()>>,
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_start(
    child_pid: u32,
    parent_xid: u32,
    width: u32,
    height: u32,
) -> *mut BonxieX11Host {
    if parent_xid == 0 || child_pid == 0 {
        return std::ptr::null_mut();
    }
    let w = width.max(2);
    let h = height.max(2);
    let wake_fd = libc::eventfd(0, libc::EFD_NONBLOCK | libc::EFD_CLOEXEC);
    let shared = Arc::new(SharedState {
        stop: AtomicBool::new(false),
        attach_state: AtomicI32::new(0),
        pending_resize: AtomicU64::new(0),
        wake_fd,
        click_cb: Mutex::new(None),
    });

    let thread_shared = Arc::clone(&shared);
    let Ok(worker) = thread::Builder::new()
        .name(format!("bonxie-x11-{child_pid}"))
        .spawn(move || run_x11_host(child_pid, parent_xid, w, h, thread_shared))
    else {
        return std::ptr::null_mut();
    };

    Box::into_raw(Box::new(BonxieX11Host {
        shared,
        worker: Some(worker),
    }))
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_stop(host: *mut BonxieX11Host) {
    if host.is_null() {
        return;
    }
    let mut boxed = Box::from_raw(host);
    boxed.shared.stop.store(true, Ordering::Release);
    boxed.shared.wake();
    if let Some(worker) = boxed.worker.take() {
        let _ = worker.join();
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_set_click_callback(
    host: *mut BonxieX11Host,
    cb: Option<extern "C" fn(usize)>,
    user_data: usize,
) {
    if host.is_null() {
        return;
    }
    if let Ok(mut guard) = (&*host).shared.click_cb.lock() {
        *guard = cb.map(|f| (f, user_data));
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_state(host: *mut BonxieX11Host) -> i32 {
    if host.is_null() {
        return -1;
    }
    (&*host).shared.attach_state.load(Ordering::Acquire)
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_focus(xid: u32) {
    if xid == 0 {
        return;
    }
    static FOCUS_CONN: OnceLock<Mutex<Option<RustConnection>>> = OnceLock::new();
    let cell = FOCUS_CONN.get_or_init(|| Mutex::new(None));
    let Ok(mut guard) = cell.lock() else {
        return;
    };
    if guard.is_none() {
        if let Ok((conn, _)) = x11rb::connect(None) {
            *guard = Some(conn);
        }
    }
    let mut ok = false;
    if let Some(conn) = guard.as_ref() {
        if conn
            .set_input_focus(InputFocus::POINTER_ROOT, xid, x11rb::CURRENT_TIME)
            .is_ok()
            && conn.flush().is_ok()
        {
            ok = true;
        }
    }
    if !ok {
        *guard = None;
        if let Ok((conn, _)) = x11rb::connect(None) {
            let _ = conn.set_input_focus(InputFocus::POINTER_ROOT, xid, x11rb::CURRENT_TIME);
            let _ = conn.flush();
            *guard = Some(conn);
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn bonxie_x11_resize(host: *mut BonxieX11Host, width: u32, height: u32) {
    if host.is_null() || width < 2 || height < 2 {
        return;
    }
    let packed = ((width as u64) << 32) | (height as u64);
    let shared = &(&*host).shared;
    shared.pending_resize.store(packed, Ordering::Release);
    shared.wake();
}

fn find_pid_window(
    conn: &RustConnection,
    root: Window,
    parent_xid: Window,
    pid_atom: u32,
    target_pid: u32,
) -> Option<Window> {
    if parent_xid != 0 {
        if let Ok(sub_tree) = conn.query_tree(parent_xid).and_then(|c| Ok(c.reply())) {
            if let Ok(tree) = sub_tree {
                for &child in tree.children.iter().rev() {
                    if window_matches_pid(conn, child, pid_atom, target_pid) {
                        return Some(child);
                    }
                }
            }
        }
    }
    let tree = conn.query_tree(root).ok()?.reply().ok()?;
    for &win in tree.children.iter().rev() {
        if window_matches_pid(conn, win, pid_atom, target_pid) {
            return Some(win);
        }
        if let Ok(sub) = conn.query_tree(win).and_then(|c| Ok(c.reply())) {
            if let Ok(sub_tree) = sub {
                for &child in sub_tree.children.iter().rev() {
                    if window_matches_pid(conn, child, pid_atom, target_pid) {
                        return Some(child);
                    }
                }
            }
        }
    }
    None
}

fn window_matches_pid(
    conn: &RustConnection,
    win: Window,
    pid_atom: u32,
    target_pid: u32,
) -> bool {
    let Ok(cookie) = conn.get_property(false, win, pid_atom, AtomEnum::CARDINAL, 0, 1) else {
        return false;
    };
    let Ok(reply) = cookie.reply() else {
        return false;
    };
    if reply.value_len == 0 {
        return false;
    }
    reply
        .value32()
        .and_then(|mut it| it.next())
        .map(|p| p == target_pid)
        .unwrap_or(false)
}

fn detach_window_to_root(
    conn: &RustConnection,
    target_win: Window,
    root: Window,
    was_reparented: bool,
) {
    if was_reparented {
        // Unmap while still override-redirect and inside parent_xid so ReparentWindow
        // does not remap it as a WM-decorated top-level window (prevents close flash).
        let _ = conn.unmap_window(target_win);
        let _ = conn.reparent_window(target_win, root, 0, 0);
    }
    let _ = conn.change_window_attributes(
        target_win,
        &ChangeWindowAttributesAux::new().override_redirect(0),
    );
    let _ = conn.flush();
}

fn apply_window_size(conn: &RustConnection, target_win: Window, width: u32, height: u32) {
    let _ = conn.configure_window(
        target_win,
        &ConfigureWindowAux::new()
            .x(0)
            .y(0)
            .width(width)
            .height(height)
            .border_width(0),
    );
}

fn run_x11_host(
    child_pid: u32,
    parent_xid: u32,
    init_w: u32,
    init_h: u32,
    shared: Arc<SharedState>,
) {
    let Ok((conn, screen_num)) = x11rb::connect(None) else {
        shared.attach_state.store(-1, Ordering::Release);
        return;
    };
    let root = conn.setup().roots[screen_num].root;

    let Ok(pid_atom_cookie) = conn.intern_atom(false, b"_NET_WM_PID") else {
        shared.attach_state.store(-1, Ordering::Release);
        return;
    };
    let Ok(pid_atom_reply) = pid_atom_cookie.reply() else {
        shared.attach_state.store(-1, Ordering::Release);
        return;
    };
    let pid_atom = pid_atom_reply.atom;

    let deadline = Instant::now() + Duration::from_secs(30);
    let mut target_win = None;
    while !shared.stop.load(Ordering::Acquire) && Instant::now() < deadline {
        if let Some(w) = find_pid_window(&conn, root, parent_xid, pid_atom, child_pid) {
            target_win = Some(w);
            break;
        }
        thread::sleep(Duration::from_millis(25));
    }

    let Some(target_win) = target_win else {
        eprintln!("[bonxie_x11] timed out waiting for X11 window of pid {child_pid}");
        shared.attach_state.store(-1, Ordering::Release);
        return;
    };

    // Strip decorations / WM override-redirect and reparent directly into the NativeControlHost child X11 window.
    // Avoid an extra UnmapWindow before ReparentWindow: X11 ReparentWindow atomically unmaps/reparents/remaps
    // if mapped, keeping Vulkan and OpenGL surfaces stable on both XWayland and Xorg/XLibre.
    let _ = conn.change_window_attributes(
        target_win,
        &ChangeWindowAttributesAux::new()
            .override_redirect(1)
            .event_mask(EventMask::STRUCTURE_NOTIFY),
    );
    let reparent_ok = conn
        .reparent_window(target_win, parent_xid, 0, 0)
        .ok()
        .and_then(|c| c.check().ok())
        .is_some();

    // Query actual parent geometry if available so initial size matches NativeControlHost exactly.
    let (mut cur_w, mut cur_h) = (init_w, init_h);
    if let Ok(geom_cookie) = conn.get_geometry(parent_xid) {
        if let Ok(geom) = geom_cookie.reply() {
            if geom.width >= 2 && geom.height >= 2 {
                cur_w = u32::from(geom.width);
                cur_h = u32::from(geom.height);
            }
        }
    }

    let config_ok = conn
        .configure_window(
            target_win,
            &ConfigureWindowAux::new()
                .x(0)
                .y(0)
                .width(cur_w)
                .height(cur_h)
                .border_width(0),
        )
        .ok()
        .and_then(|c| c.check().ok())
        .is_some();
    let map_ok = conn
        .map_window(target_win)
        .ok()
        .and_then(|c| c.check().ok())
        .is_some();
    let mut grab_ok = reparent_ok && config_ok && map_ok;
    if grab_ok {
        // Grab only actual mouse buttons (left/middle/right) so scroll wheel (Button4/5/6/7)
        // bypasses the synchronous grab and never fires spurious click callbacks.
        for btn in [ButtonIndex::M1, ButtonIndex::M2, ButtonIndex::M3] {
            if conn
                .grab_button(
                    false,
                    target_win,
                    EventMask::BUTTON_PRESS,
                    GrabMode::SYNC,
                    GrabMode::ASYNC,
                    0u32,
                    0u32,
                    btn,
                    ModMask::ANY,
                )
                .ok()
                .and_then(|c| c.check().ok())
                .is_none()
            {
                grab_ok = false;
                break;
            }
        }
    }
    if !reparent_ok || !config_ok || !map_ok || !grab_ok {
        eprintln!(
            "[bonxie_x11] failed to reparent/configure/map/grab window {target_win} for pid {child_pid}"
        );
        detach_window_to_root(&conn, target_win, root, reparent_ok);
        shared.attach_state.store(-1, Ordering::Release);
        return;
    }
    if conn.flush().is_err() {
        detach_window_to_root(&conn, target_win, root, true);
        shared.attach_state.store(-1, Ordering::Release);
        return;
    }
    shared.attach_state.store(1, Ordering::Release);

    let x11_fd = conn.stream().as_raw_fd();
    let wake_fd = shared.wake_fd;
    let mut destroyed = false;

    while !shared.stop.load(Ordering::Acquire) {
        let packed = shared.pending_resize.swap(0, Ordering::AcqRel);
        if packed != 0 {
            let rw = (packed >> 32) as u32;
            let rh = (packed & 0xFFFF_FFFF) as u32;
            if rw >= 2 && rh >= 2 && (rw != cur_w || rh != cur_h) {
                cur_w = rw;
                cur_h = rh;
                apply_window_size(&conn, target_win, cur_w, cur_h);
                let _ = conn.flush();
            }
        }

        loop {
            match conn.poll_for_event() {
                Ok(Some(Event::ButtonPress(bp))) => {
                    let _ = conn.set_input_focus(InputFocus::POINTER_ROOT, target_win, bp.time);
                    let _ = conn.allow_events(Allow::REPLAY_POINTER, bp.time);
                    let _ = conn.flush();
                    if let Ok(guard) = shared.click_cb.lock() {
                        if let Some((cb, user_data)) = *guard {
                            cb(user_data);
                        }
                    }
                }
                Ok(Some(Event::DestroyNotify(dn))) if dn.window == target_win => {
                    destroyed = true;
                    shared.attach_state.store(-1, Ordering::Release);
                    break;
                }
                Ok(Some(_)) => {}
                Ok(None) => break,
                Err(_) => {
                    destroyed = true;
                    shared.attach_state.store(-1, Ordering::Release);
                    break;
                }
            }
        }
        if destroyed || shared.stop.load(Ordering::Acquire) {
            break;
        }

        let mut pfds = [
            libc::pollfd {
                fd: x11_fd,
                events: libc::POLLIN,
                revents: 0,
            },
            libc::pollfd {
                fd: wake_fd,
                events: libc::POLLIN,
                revents: 0,
            },
        ];
        let nfds = if wake_fd >= 0 { 2 } else { 1 };
        unsafe {
            libc::poll(pfds.as_mut_ptr(), nfds, 250);
            if wake_fd >= 0 && pfds[1].revents != 0 {
                let mut val = 0u64;
                libc::read(wake_fd, &mut val as *mut u64 as *mut libc::c_void, 8);
            }
        }
    }

    if !destroyed {
        detach_window_to_root(&conn, target_win, root, true);
    }
}
