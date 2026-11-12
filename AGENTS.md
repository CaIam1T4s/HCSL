# Repository guidance

This repository publishes product code; user-facing documentation will be added
in a later version. Internal planning notes, manual-verification records, and
development skills are kept local (see `.gitignore`) and are not published.

Keep scaffold directories separate from implemented projects: only add a project
to the solution when its project file and implementation exist. Report build and
runtime verification limits accurately.

Architecture and placement rules follow the existing solution and project files;
do not add platform logic to ViewModels or UI projects, and keep the dependency
direction `Application → Core`, `Infrastructure → Application + Core`.
