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

## Verification

Build only the affected project (`dotnet build <project> --no-restore -v:q`) and run only the
matching test section (`dotnet run --project tests/<suite> -- --only <section>`).

**Never run the full suite** (`./build/test-fast.sh full`) without explicit permission from the
operator. When permission is given, say which sections will run and why before starting, and
report build success separately from tests and from real runtime verification.

## Development skills

`skills/` holds the development skills that stay out of the published tree (see `.gitignore`).
Load the matching skill before the work starts, not after it:

| Skill | Read it when |
|---|---|
| `skills/audit-before-change/SKILL.md` | any bug fix, feature, refactor or long debugging session: evidence-first audit table, implementation order, small gates, end-of-session cleanup |
| `skills/cs2-project-architecture/SKILL.md` | placing or moving host, player client, TUI, daemon or plugin code; project dependencies and feature boundaries |
| `skills/hcsl-overlay-surfaces/SKILL.md` | adding or reviewing any dialog, drawer, popover, menu, toast, tooltip or other floating surface: 80% material opacity and backdrop blur |
| `skills/tkcode-arc/SKILL.md` | any Avalonia/TKCode.Arc screen: component choice, design tokens, motion, copy, accessibility; finish by running its `checklist.md` |

## Known UI limitations

- **Arc table row rules at fractional scaling**: with `RowHeight = 52` and a display scale such as
  1.104, Spyglass measured row pitches of 51.6 / 51.7 / 52.5, so the 1px rule Arc draws inside a
  row is sometimes blended away between two rows' backgrounds. A self-drawn, pixel-snapped
  separator layer was tried and reverted: the occasional missing rule is accepted for now. Do not
  re-raise it without an operator decision.
