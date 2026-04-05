using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.Tray;

public class Main : IAsyncPlugin, IContextMenu {
    private string _appIconPath = "Images/icon.png";

    public async Task InitAsync(PluginInitContext context) {
        _appIconPath = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, _appIconPath);
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token) {
        return new List<Result> {
            new Result {
                Title = "Example Tray App",
                SubTitle = "This is a placeholder for a tray application",
                IcoPath = _appIconPath,
                Action = _ => {
                    // Implement the action to interact with the tray application
                    return true;
                }
            }
        };
    }

    public List<Result> LoadContextMenus(Result selectedResult) {
        return new List<Result> {
            new Result {
                Title = "Open",
                IcoPath = _appIconPath,
                Action = _ => {
                    // Implement the action to open the selected tray application
                    return true;
                }
            },
            new Result {
                Title = "Close",
                IcoPath = _appIconPath,
                Action = _ => {
                    // Implement the action to close the selected tray application
                    return true;
                }
            }
        };
    }
}