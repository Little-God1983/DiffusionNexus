# Copilot Instructions

This is an Avalonia Project Written in C# / .net 10

## General Guidelines

* When a bug is found, always check if a unit test can be created to cover/reproduce it before fixing. Ensure that the unit test effectively captures the bug scenario.
* When a refactoring task is issued then mark the method/function/class you are refactoring as obsolete and to be removed later to avoid code duplication/dead code.
* When the database shall be modified execute the publish.ps1 script to make sure there is a last backup of the app with a working database.
* When A Keyboard Shortcut is Added make sure its documented in a file called DiffusionNexus.UI\\Doc\\Shortcuts.md
* When fixing a failing Unit test, check thoroughly if its the unit test that is in need of fixing or if its the code that has a bug.

## Code Style

* Use specific formatting rules
* Follow naming conventions

## Project-Specific Rules

* The ComfyUI Qwen3\_VQA custom node stores its models in ComfyUI's models/prompt\_generator folder, not in the HuggingFace cache.
* While the main goal is Windows, create a comment TODO: Linux Implementation for Task X in the code. Also, make sure it is open to extension for the Linux implementation
