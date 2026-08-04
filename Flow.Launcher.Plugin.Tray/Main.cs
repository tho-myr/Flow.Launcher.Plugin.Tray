using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.Tray;

public class Main : IAsyncPlugin, IContextMenu
{
    private string _appIconPath = "Images/icon.png";
    private string _iconCacheDir = string.Empty;

    public async Task InitAsync(PluginInitContext context)
    {
        _appIconPath = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, _appIconPath);
        _iconCacheDir = Path.Combine(context.CurrentPluginMetadata.PluginDirectory, "IconCache");
        Directory.CreateDirectory(_iconCacheDir);
        await Task.CompletedTask;
    }

    public async Task<List<Result>> QueryAsync(Query query, CancellationToken token)
    {
        var trayIcons = await Task.Run(() => TrayHelper.EnumerateAll(_iconCacheDir), token);
        var results = new List<Result>();

        foreach (var icon in trayIcons)
        {
            if (token.IsCancellationRequested) break;
            if (!string.IsNullOrEmpty(query.Search) &&
                !icon.Tooltip.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
                continue;

            var snapshot = icon;
            results.Add(new Result
            {
                Title = icon.Tooltip.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "),
                SubTitle = icon.ExecutablePath,
                IcoPath = string.IsNullOrEmpty(icon.IconPath) ? _appIconPath : icon.IconPath,
                ContextData = snapshot,
                Action = ctx =>
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        TrayHelper.SimulateClick(snapshot, rightClick: false);
                    });
                    return true;
                }
            });
        }

        if (results.Count == 0)
        {
            foreach (var msg in TrayHelper.DiagLog)
            {
                results.Add(new Result
                {
                    Title = msg,
                    SubTitle = "Diagnostic",
                    IcoPath = _appIconPath,
                    Action = _ => false
                });
            }
        }

        return results;
    }

    public List<Result> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult.ContextData is not TrayIconInfo info) return [];

        return
        [
            new Result
            {
                Title = "Open context menu",
                SubTitle = "Right-click the tray icon",
                IcoPath = _appIconPath,
                Action = ctx =>
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(300);
                        TrayHelper.SimulateClick(info, rightClick: true);
                    });
                    return true;
                }
            }
        ];
    }
}