# Icon Saver

Icon Saver is a Windows desktop app that saves and restores the positions of your desktop icons. It keeps your 10 most recent layouts, with an optional description to help you find the one you want.

## Save and restore a layout

1. Arrange your desktop icons and save the current layout in Icon Saver.
2. Select a saved layout from the history when you want to restore it.

Hover over a saved layout to see the number of screens and their resolutions. If some matched icons belong to unavailable screens, Icon Saver asks before restoring the remaining icons on their active destinations. If any destination screen is narrower or shorter than saved, Icon Saver stops before moving icons. Older layouts still restore using their original coordinates.

If the history becomes unreadable, use **Recover history** to restore the last saved copy or start a new history. Icon Saver keeps the unreadable file in `%LOCALAPPDATA%\IconSaver`.

## Build from source

On Windows, install the .NET 11 SDK and run `dotnet build .\app\IconSaver.csproj -m:1` from this folder.

Run `dotnet test .\tests\IconSaver.Tests.csproj -m:1` to check history recovery and icon matching.

## License and donations

Icon Saver is free to use under its [Donationware license](Licence.md). If you would like to support its development, you can make an optional donation through [Buy Me a Coffee](https://buymeacoffee.com/knhsynths).
