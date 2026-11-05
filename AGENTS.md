# Repository guidance

For changes to this repository's architecture, project layout, feature placement,
project references, or shared CS2 launcher behavior, read and apply
[cs2-project-architecture](skills/cs2-project-architecture/SKILL.md).

This is a repository-local skill stored outside `.agents` because that directory
is read-only in the development environment. It is not installed as a global skill.

Keep scaffold directories separate from implemented projects: only add a project
to the solution when its project file and implementation exist. Report build and
runtime verification limits accurately.
