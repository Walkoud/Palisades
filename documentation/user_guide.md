# Palisades User Guide

Welcome to the **Palisades** User Guide! Palisades is a modern desktop organizer and productivity suite designed to keep your Windows desktop clutter-free, stylized, and functional.

---

## 🚀 Key Features

* **Desktop Containers**: Organize your shortcuts, files, and programs into elegant, transparent, glassmorphic boxes.
* **Folder Portals**: Mirror real folders on your drive directly on the desktop with real-time file system synchronization.
* **Desktop Widget/Gadget Suite**: Add utility widgets like Post-it notes, stylized clocks, and system monitors (CPU/RAM).
* **Desktop Overlay**: View, select, group, and drag your actual desktop icons on a modern canvas with snapping, locking, and multi-monitor layout persistence.
* **Multi-Language Support**: Switch dynamically between **English** and **Français** from the Dashboard.

---

## 📁 1. Desktop Containers

Palisades allows you to keep shortcuts tidy using customizable grid panels.

### Creating a Container
1. Open the **Palisades Dashboard** (or right-click the Palisades system tray icon).
2. Click **New +** (or **Nouveau +** in French) on the top bar or sidebar.
3. Select **Standard Container**. A new container will be placed on your desktop.
4. Alternatively, you can double-click on any empty area of the desktop overlay and draw a rectangle (rubber-band drag-to-create) to spawn a container.

### Managing Shortcuts
* **Drag-and-Drop**: Drag files, folders, or executables directly from Windows Explorer or the desktop overlay into any container.
* **Reordering**: Drag items inside a container to rearrange them.
* **Sorting**: Click the Hamburger menu `☰` on the container header and select **Sort all** to sort items alphabetically, or configure **Auto-Sort Categories** in the Dashboard to automatically pull matching file types from the desktop.

### Container Settings & Customization
Right-click a container header and choose **Edit Properties** to open the properties panel in the Dashboard. From here you can customize:
* **Visual Theme**: Presets like *Glass, Dark, Midnight, Amber, Frost, Forest, or Plum*.
* **Custom Colors**: Manually set custom background, border, header background, and text colors.
* **Opacity**: Adjust background transparency when inactive or hovered.
* **Borders & Corner Radius**: Toggle sharp or rounded corners and show/hide borders.
* **Auto-Hide**: Collapse the container into a thin title bar when your mouse leaves, expanding instantly when hovered.
* **Lock Position**: Prevent accidental dragging or resizing.

---

## 🔄 2. Folder Portals

Folder Portals are dynamic windows that display the live contents of a directory on your hard drive.

### Creating a Folder Portal
1. Click **New +** on the Dashboard.
2. Select **Folder Portal**.
3. Choose the directory you want to mirror in the folder selector.
4. Any files added, renamed, or deleted in that folder will reflect instantly on your desktop using Palisades' built-in file watcher.

### Changing Folders
If you want to mirror a different folder:
1. Open **Edit Properties** for the portal.
2. Under the **Folder Portal** tab, click **Change Folder**.
3. Select the new path.

---

## 📝 3. Post-it Notes & Widgets

Palisades supports customizable notes and dashboard widgets to keep notes and stats handy.

### Post-it Notes
* **Adding a Note**: Click **New +** -> **Post-it Note**.
* **Editing Content**: Click inside the note and start typing. Notes auto-save every 5 seconds.
* **Customizing Color**: Click the Hamburger menu `☰` inside the note, select **Color**, and choose a style.
* **Font Size**: Change text size (Small, Medium, Large, X-Large, MAX) from the note menu.
* **Moving & Resizing**: Drag by the header to move, or drag the bottom-right corner to resize.

### Clock Gadget
* Stylized overlay clock displaying time.
* Supports switching between **24-hour format** and **showing seconds**.
* Custom text color presets (Ice Blue, Matrix Green, Cyber Red, White, Amber Orange).

### System Monitor
* Real-time tracker for **CPU** and **Memory (RAM)** usage.
* Adjustable refresh rates (from 0.5s up to 5.0s).

---

## 🖥️ 4. Desktop Overlay & Icon Management

Palisades has a built-in Desktop Service that hides the default Windows desktop icons and renders them inside its own overlay canvas.

### Hiding / Showing Desktop Icons
* In the **App Settings** or from the system tray menu, you can toggle **Show Desktop Icons** / **Hide Desktop Icons**.
* When hidden, Palisades takes over and displays unassigned desktop shortcuts on its overlay.

### Overlay Canvas Controls
* **Selecting Items**: Click and drag a selection rectangle (rubber-band) on the empty desktop wallpaper to select multiple items.
* **Group Drag**: Drag any selected icon to move the entire group.
* **Snapping**: Icons automatically align to a virtual layout grid to keep alignment neat. Hold **Shift** while dragging to bypass snapping.
* **Return to Desktop**: Right-click an icon on the overlay and use the context menu.

---

## 📸 5. Snapshots & Configuration Backups

If you configure your desktop layout across multiple monitors, resolutions, or setups, you can save the layout as a snapshot.

* **Capture Snapshot**: In the **Snapshots** tab in the Dashboard, click **+ Capture**. Type a name and save.
* **Auto-Snapshots**: Palisades automatically captures a snapshot when resolution changes (e.g. plugging/unplugging monitors).
* **Restore/Delete**: Select any snapshot in the list to restore your layout instantly, or delete old records.
* **Configuration Backup**: Under **App Settings**, use **Export Config** to save your entire workspace layout and notes to a `.json` file. Use **Import Config** to restore it on a new PC.
