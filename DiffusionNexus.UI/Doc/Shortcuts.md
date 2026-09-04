# Keyboard Shortcuts

## Image Viewer Dialog

| Key | Action | Context |
|-----|--------|---------|
| Left Arrow | Previous image | Always |
| Right Arrow | Next image | Always |
| Space | Next image | Always |
| Escape | Close viewer | Always |
| W / Up Arrow | Mark Ready | Rating controls visible |
| S / Down Arrow | Mark Trash | Rating controls visible |
| C | Clear Rating | Rating controls visible |
| F | Toggle Favorite | Generation Gallery viewer |
| E | Send to Image Editor | Image only |
| T | Send to Captioning | Image only |
| Delete | Delete image | Always |
| Ctrl+S | Save caption | Rating controls visible |
| Ctrl+Z | Revert caption | Rating controls visible |
| M | Toggle metadata panel | Always |

## Image Editor

| Key | Action | Context |
|-----|--------|---------|
| Shift (held) | Constrain the stroke to a straight line | Freehand drawing tool active |
| Ctrl (held) | Constrain the shape to a square / circle | Shape tool active |
| Ctrl+Enter | Generate the inpaint | Inpainting tool active |
| Enter | Commit the placed text | Text tool active with placed text |
| Escape | Cancel the placed text | Text tool active with placed text |
| Enter | Commit the placed shape | Shape tool active with placed shape |
| Escape | Cancel the placed shape | Shape tool active with placed shape |
| Enter | Apply canvas extension | Extend tool active |
| Escape | Reset canvas extension (tool stays open) | Extend tool active |
| C / Enter | Apply the crop | Crop tool active with a region |
| Escape | Clear the crop region | Crop tool active with a region |

## Diffusion Canvas

| Key | Action | Context |
|-----|--------|---------|
| Middle-drag | Pan the canvas | Always |
| Space (held) + left-drag | Pan the canvas | Nothing staged (see below) |
| Mouse wheel | Zoom about the cursor | Always |
| Alt (held) | Place the box off the grid (position only — the size always snaps to the model's lattice) | Dragging the generation box |
| F | Fit everything on screen | Not typing in a text box |
| 1 | Zoom to 1:1 (one generated pixel per screen pixel) | Not typing in a text box |
| B | Centre the view on the generation box | Not typing in a text box |
| G | Toggle the dot grid | Not typing in a text box |
| Escape | Abandon the in-progress box drag | Dragging the generation box |
| Right-click a result | Open its menu (Delete result) | Pointer over an accepted result |
| Left / Right Arrow | Previous / next candidate | Staging strip has candidates |
| Space (held) | Flip the candidate away to compare it against the canvas underneath | Staging strip has candidates |
| Enter | Accept the candidate onto the canvas | Staging strip has candidates |
| Delete | Discard the candidate | Staging strip has candidates |

> **Space does two things by design.** While candidates are staged it is the compare gesture — a
> variant cannot be judged against nothing — and drag-to-pan is disarmed. With the strip empty there
> is nothing to compare, so it arms drag-to-pan instead. Both behaviours come from issue #518.
>
> Every shortcut here is suppressed while the caret is in a text box, so the prompt field keeps its
> spaces, arrows and returns.
