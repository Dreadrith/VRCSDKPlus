(v1.5.0)
--------
- [Feature] Added Keyboard commands (shortcuts) and context menus (right click) to Parameters and Menu Editors
	- Parameter editor can now handle Ctrl+D and 'DEL' for Duplicate and Delete respectively.
	- Menu editor can now handle Ctrl+C, Ctrl+X, Ctrl+V, Ctrl+D, and 'DEL' for Copy, Cut, Paste, Duplicate and Delete respectively.
- [Improvement] Parameters and Controls now have a 'smart increment' to them. (@buddy)
	- Newly added parameters will be unique by appending/incrementing numbers.
	- Duplicated control's name will count up if it has a number.
	- Duplicated control's parameter name will count up if it's a bool and has a number (Disabled when holding shift).
	- Duplicated control's parameter value will count up if it's an int (Disabled when holding shift).
- [Improvement] Submenus created through editor will use the control name instead of the parent menu name. (@m1s0)
- [Fix] Fixed parameter editor rendering when unity is not in fullscreen.
- [Misc] Added unity version to package.json to suppress VCC warning about the version.

(v1.4.0)
--------
- [Feature] Double clicking a submenu control will now switch to that submenu
- [Improvement] Decent performance boost to parameters editor.
- [Fix] Fixed compatibility with 2022 SDK
- [Fix] Fixed merged parameters editing the source parameters
- [UI] Buttons will now change cursor
- [Misc] Changelog will follow the same format as my other scripts from here on.

## [1.3.5] - 2023-11-30
### Fixed
- Fixed the VPM structure of the package

## [1.3.4] - 2023-04-02
### Fixed
- Fixed a code issue that used exponentially more performance the more ex parameters you have

## [1.3.3] - 2023-04-02
### Added
- Support for 'synced' field to Expression Parameters
- Delete Expression Parameters with the Delete button when focused
- Shortcut to search expression parameters: Ctrl + F
### Fixed
- Fixed Text field in Expression Parameters always selecting entire text when clicked
- Fixed case where Editors might not override and throw an error. Possibly also crashing.
- Fixed script throwing an error when using a debugger through an IDE
### UI
- Reworked UI to be more stable and less funky when scrunched
- Type of the controller parameter is now displayed on the expression parameters

## [1.3.1] - 2022-08-27
### Added
- Added "New" next to Empty SubMenu to quickly create a new SubMenu
### Fixed
- Fixed 'Toggle Editor' not working as intended if the VRCSDK is imported through VCC

## [1.3.0] - 2022-08-27
### Added
- Added Styling, allows bold, italic and color selection

## [1.2.1] - 2022-06-28
### Added
- Added parameter filter in expression parameters
### Fixed
- Fixed Parameter value field missing in non-compact mode

## [1.2.0] - 2022-06-17
### Added
- Implemented Expressions Menu editor
### Improvements
- Improved performance of Expression Parameters editor

## [1.0.1] - 2022-06-03
### Fixed
- Fixed parameters in a row all showing warnings after the first warning
- Fixed cleanup removing all parameters if no descriptor is found/selected
- Fixed error when merging a newly created Parameters asset

## [1.0.0] - 2022-05-29
### Added
- Implemented official [Unity Package layout] (https://docs.unity3d.com/2019.4/Documentation/Manual/cus-layout.html) 