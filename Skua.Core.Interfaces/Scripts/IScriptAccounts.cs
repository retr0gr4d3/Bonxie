namespace Skua.Core.Interfaces;

/// <summary>
/// Represents a managed account and its tags.
/// </summary>
/// <param name="Username">Login name of the account.</param>
/// <param name="Password">Always empty: saved passwords are not given to scripts.</param>
/// <param name="DisplayName">Name shown for the account in the manager.</param>
/// <param name="Tags">Tags set on the account.</param>
public record ManagedAccount(string Username, string Password, string DisplayName, List<string> Tags);

/// <summary>
/// Provides methods for scripts to access and manage account tags.
/// </summary>
public interface IScriptAccounts
{
    /// <summary>
    /// Gets all tags for the current logged-in account.
    /// </summary>
    /// <returns>A list of tags, or empty list if no tags exist.</returns>
    List<string> GetTags();

    /// <summary>
    /// Gets all tags for a specific account by username.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <returns>A list of tags, or empty list if account not found or has no tags.</returns>
    List<string> GetTags(string username);

    /// <summary>
    /// Checks if the current logged-in account has the specified tag.
    /// </summary>
    /// <param name="tag">The tag to check for.</param>
    /// <returns>True if the account has the tag, false otherwise.</returns>
    bool HasTag(string tag);

    /// <summary>
    /// Checks if a specific account has the specified tag.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tag">The tag to check for.</param>
    /// <returns>True if the account has the tag, false otherwise.</returns>
    bool HasTag(string username, string tag);

    /// <summary>
    /// Adds a tag to the current logged-in account.
    /// </summary>
    /// <param name="tag">The tag to add.</param>
    /// <returns>True if the tag was added, false if it already exists.</returns>
    bool AddTag(string tag);

    /// <summary>
    /// Adds a tag to a specific account.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tag">The tag to add.</param>
    /// <returns>True if the tag was added, false if account not found or tag already exists.</returns>
    bool AddTag(string username, string tag);

    /// <summary>
    /// Removes a tag from the current logged-in account.
    /// </summary>
    /// <param name="tag">The tag to remove.</param>
    /// <returns>True if the tag was removed, false if it didn't exist.</returns>
    bool RemoveTag(string tag);

    /// <summary>
    /// Removes a tag from a specific account.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tag">The tag to remove.</param>
    /// <returns>True if the tag was removed, false if account not found or tag didn't exist.</returns>
    bool RemoveTag(string username, string tag);

    /// <summary>
    /// Adds multiple tags to the current logged-in account.
    /// </summary>
    /// <param name="tags">The tags to add.</param>
    void AddTags(params string[] tags);

    /// <summary>
    /// Adds multiple tags to a specific account.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tags">The tags to add.</param>
    /// <returns>True if any tags were added, false if account not found.</returns>
    bool AddTags(string username, params string[] tags);

    /// <summary>
    /// Removes multiple tags from the current logged-in account.
    /// </summary>
    /// <param name="tags">The tags to remove.</param>
    void RemoveTags(params string[] tags);

    /// <summary>
    /// Removes multiple tags from a specific account.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tags">The tags to remove.</param>
    /// <returns>True if any tags were removed, false if account not found.</returns>
    bool RemoveTags(string username, params string[] tags);

    /// <summary>
    /// Sets all tags for the current logged-in account, replacing any existing tags.
    /// </summary>
    /// <param name="tags">The tags to set.</param>
    void SetTags(params string[] tags);

    /// <summary>
    /// Sets all tags for a specific account, replacing any existing tags.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <param name="tags">The tags to set.</param>
    /// <returns>True if successful, false if account not found.</returns>
    bool SetTags(string username, params string[] tags);

    /// <summary>
    /// Clears all tags from the current logged-in account.
    /// </summary>
    void ClearTags();

    /// <summary>
    /// Clears all tags from a specific account.
    /// </summary>
    /// <param name="username">The username of the account.</param>
    /// <returns>True if successful, false if account not found.</returns>
    bool ClearTags(string username);

    /// <summary>
    /// Gets all managed accounts.
    /// </summary>
    /// <returns>List of managed accounts with their tags. <see cref="ManagedAccount.Password"/> is always empty.</returns>
    List<ManagedAccount> GetAllAccounts();
}
