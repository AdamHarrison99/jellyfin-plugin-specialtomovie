using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

/// <summary>
/// Deletes a linked item and the files behind it through Jellyfin.
/// </summary>
/// <remarks>
/// Deletion is delegated to <see cref="ILibraryManager.DeleteItem"/> rather than done with raw file
/// I/O so that Jellyfin removes the database row, the images and the user data along with the file,
/// and so that its own event pipeline runs. This lived as four near-identical private copies across
/// the controller, the detection service and the library event handler; a single definition keeps
/// the null and missing-item guards from drifting apart between call sites.
/// </remarks>
internal static class LinkedItemDeleter
{
    /// <summary>
    /// Deletes the item and its files, if it still exists.
    /// </summary>
    /// <remarks>
    /// Never throws. Callers are event handlers, scheduled tasks and API endpoints for which a
    /// failed delete must not abort the surrounding work, so a failure is logged and swallowed.
    /// </remarks>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">Logger for the calling component.</param>
    /// <param name="itemId">The item to delete; null and empty are no-ops.</param>
    /// <returns>True if an item was found and deleted.</returns>
    public static bool DeleteWithFiles(ILibraryManager libraryManager, ILogger logger, Guid? itemId)
    {
        if (itemId is null || itemId == Guid.Empty)
        {
            return false;
        }

        var item = libraryManager.GetItemById(itemId.Value);
        if (item == null)
        {
            return false;
        }

        try
        {
            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
            logger.LogInformation("Deleted {Name} with files via Jellyfin", item.Name);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete item {Id} via Jellyfin", itemId);
            return false;
        }
    }
}
