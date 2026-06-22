# Task Checklist

## 1. Always Allow Resizing (Only Hide Grippers)
- [x] Modify `ContainerControl.xaml` (always show grid, hide path)
- [x] Modify `NoteControl.xaml` & `NoteControl.xaml.cs` (always allow resize, hide rectangle)
- [x] Modify `PluginGadgetWrapper.xaml` & `PluginGadgetWrapper.xaml.cs` (always allow resize, hide path)

## 2. Post-it Note Toolbar Layout & Visual Fix
- [x] Modify `PostItGadgetPlugin.cs` (toolbar to bottom, explicit button styling to prevent MD clipping, font size increase/decrease buttons)

## 3. Container Type Prompt on Drawing (Refined)
- [x] Create custom programmatic `_drawMenuPopup` Border and layout inside `DesktopOverlayWindow.xaml.cs`
- [x] Keep `_selectRect` visible while menu is open
- [x] Hook `MouseLeave` event on the menu to cancel both the menu and drawing
- [x] Modify `App.xaml.cs` to handle container creation using the direct enum parameter passed from the overlay menu
- [x] Refine Drawing Overlay Custom Hover Menu visual design to look exactly like right-click context menu (dark theme, styled items) and position at mouse cursor.

## 4. Background Context Menu working directory fix
- [x] Modify `ContainerControl.xaml.cs` (set `workingDir = isBackground ? filePath : Path.GetDirectoryName(filePath)`)

## 5. Dashboard "New +" Dropdown Menu
- [x] Replace the large UniformGrid Quick Actions panel in `ArcticShelterWindow.xaml` with a single, styled premium "New +" button that triggers a contextual menu on left click.
- [x] Map commands to create Standard, SVG, and Folder Portal containers, and spawn Post-it, Clock, and Monitor gadgets.

## 6. SVG Button Container bugs & inline customizer
- [x] Modify `ShowWindowsContextMenu` in `ContainerControl.xaml.cs` to allow empty targets and customize SVG buttons even before target paths are set.
- [x] Wrap programmatic dialog `Owner` setting in try-catch in both `ContainerControl.xaml.cs` and `ArcticShelterWindow.xaml.cs` to prevent WPF exceptions.
- [x] Implement `INotifyPropertyChanged` in `ShortcutItem` for real-time live preview updates.
- [x] Add "Configure SVG Buttons" category at the top of the properties panel in `ArcticShelterWindow.xaml` (visible only when an SVG Container is selected).
- [x] Embed horizontal listbox showing live SVG/image preview of the container's buttons with "+ Add Button" functionality.
- [x] Implement inline form to customize Button Name, Target Path/URL (with File/Folder browsing), Arguments, Hotkey capture, and Custom Image Icon (supporting PNG, JPG, BMP, and SVG files).
- [x] Build inline raw SVG XML editor popup dialog.
- [x] Update SVG Button template in `ContainerControl.xaml` to render imported raster image files (PNG/JPG/BMP) using `FilePathToImageConverter` when `SvgContent` is absent.

## 7. Refinements & Fixes (Current Task)
- [x] Fix mouse click swallowing on the Drawing Overlay Hover Menu (`DesktopOverlayWindow.xaml.cs`)
- [x] Align hover menu layout style with container context menu (`DesktopOverlayWindow.xaml.cs`)
- [x] Add `HideAddSvgButton` property to `ContainerModel` and `ContainerViewModel`
- [x] Add a CheckBox to hide the "+" button in the Dashboard's SVG config panel (`ArcticShelterWindow.xaml`)
- [x] Bind the visibility of the "+" button in `ContainerControl.xaml` to `HideAddSvgButton`
- [x] Fix the "+" button click dialog owner in `ContainerControl.xaml.cs` to use active Dashboard/MainWindow
- [x] Add transform parsing support to `SvgRenderer.cs` (translate, scale, matrix, rotate, skew)
- [x] Add group-level shape property inheritance (`fill`, `stroke`) to `SvgRenderer.cs`
- [x] Verify clean compilation and perform manual tests (YouTube SVG, "+" hiding, menu clicking)
