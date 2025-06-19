# DiffusionNexus

This repository contains an Avalonia UI application.

## Sidebar behavior

The sidebar no longer overlays the main content. Instead, it is part of a two column grid. When the hamburger button is pressed, the sidebar column expands to `200` pixels and the main content scales down slightly to provide focus.

Animation durations and sizes are defined in `MainWindow.axaml` and can be tweaked via the `DoubleTransition` durations and the `SidebarWidth`/`MainContentScale` properties in `MainWindowViewModel`.
