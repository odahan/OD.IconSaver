# Icon Saver behavior

## Original evidence

- Packed entry at `0x7fe670` decodes 4,181,441 bytes into the image at `0x401000`. The desktop logic was inspected in the statically expanded payload. The original executable was not used to generate application code.
- The manual Save handler at `0x655bb0` delegates through `0x655efc` and `0x667438` to the list-view reader at `0x665a20`. It reads the item count (`0x1004`), item text (`0x102d`), and coordinates (`0x1010`) and stores the results.
- Restore latest (`0x657560`) and Restore selected (`0x657acc`) choose resolution-specific Registry data through `0x658a50` and `0x658bc8`, respectively. The original desktop finder at `0x6672c8` uses `SHELLDLL_DefView` and `SysListView32`. The restore routine at `0x66648c` reads list-view state (`0x1037`) and sets item coordinates with `0x100f`.
- Icon Saver ports the indexed count, label, and coordinate loop to the Windows Shell folder view. Restore resolves items against the live view and places them through `IFolderView::SelectAndPositionItems`, which lets modern Explorer retain the positions. [Microsoft explains why direct list-view moves are lost on modern Windows](https://devblogs.microsoft.com/oldnewthing/20211122-00/?p=105948).
- The original prunes saved layouts in `0x656fa8`. The fixed ten-layout limit, global JSON history, and full timestamp display in Icon Saver come from the project brief.

## Icon Saver rules

| Rule | Value | Source |
| --- | --- | --- |
| Positions saved | Current Windows desktop icon coordinates | Original functions `0x655bb0` and `0x665a20`; project brief |
| Restore input | One selected saved layout | Original functions `0x657acc` and `0x658bc8`; project brief |
| History size | 10 layouts | Project brief |
| Delete layout | Permanently remove the selected saved layout after confirmation in a dark dialog | User requests |
| List order | Newest first | Project brief |
| Layout name | Local date, time to milliseconds, and UTC offset | Project brief; display format chosen for Icon Saver |
| Layout description | Optional text of up to 80 characters, entered in a dark save dialog and displayed beside the timestamp as read-only text | User request; length limit chosen for Icon Saver |
| Screen images in history | Disabled | Project brief |
| License management | Disabled | Project brief |
| Auto-arrange | If a move is needed, ask the user to turn off Windows auto-arrange | Original English resources, messages 129 and 130 |
| Item matching | Shell identity. For older layouts with raw Shell identifiers, allow a display label unique in both layouts after confirmation. | Original function `0x66648c` uses the item label. Icon Saver also preserves Shell identities for existing layouts and duplicate labels. |
| Items absent from the selected layout | Remain untouched | Icon Saver implementation |
| Screen geometry | Save each active desktop screen's resolution and each icon's offset on its screen. Restore to the same screen when identifiable, or to an available replacement in the same desktop position if the screen count has not decreased. | Icon Saver implementation |
| Screen compatibility | Confirm partial restores if a matched icon's screen is unavailable. Refuse the restore if any destination screen is narrower or shorter than saved. Layouts without screen information use their original desktop-view coordinates. | User request |
| History recovery | Keep a recovery copy of the saved history and preserve the unreadable file when recovering | Icon Saver implementation |
