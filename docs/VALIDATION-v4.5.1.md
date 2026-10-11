# v4.5.1 icon fix validation

The screenshot showed Edge falling back to a generic icon after Chrome recovered.
The exact first native failure from v4.5.0 was not logged. The old loader lacked
explicit COM initialization required by SHGetFileInfo and treated a fallback as
successful completion with no retry. Both defects are corrected.

Local validation:

- Production and test projects build without warnings or errors.
- The full regression suite passes, including unchanged Focus, window placement,
  clicks, expansion, FIXED, scrolling, screenshot isolation and settings behavior.
- Chrome and Edge each pass 25 background icon loads, pixel-matching the respective
  installed executable resource. Actual extracted PNGs are retained outside Git.
- Native concurrent loads return separately owned, editable/disposable bitmaps.
- Transient failures recover on the third attempt and publish only one real image.
- Closing during retry cancels subsequent work.
- One hundred late-result/close cases dispose their bitmaps and never update a closed UI.
- The previous CI failure was the test reading Bitmap.Width concurrently with
  disposal. The test now waits for cleanup completion before checking disposal.

The CI-produced executable still needs its download hash and provenance checked,
then an independent local launch with the existing settings and desktop shortcut.
The 24-hour/manual release gate has not been completed for this new executable.
