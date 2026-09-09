using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Services;

// The one definition of "delete a linked item and its files".
// See agentic/ARCHITECTURE.md, "Deletion and the ItemRemoved cascade".
internal static class LinkedItemDeleter
{
    // ! Never throws: a failed delete must not abort the caller's surrounding work.
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
