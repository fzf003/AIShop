# Triage Labels

Five canonical roles, each label string equal to its name:

| Role | Label |
|------|-------|
| Needs triage | `needs-triage` |
| Needs info | `needs-info` |
| Ready for agent | `ready-for-agent` |
| Ready for human | `ready-for-human` |
| Wontfix | `wontfix` |

## Conventions

- Apply `needs-triage` to new issues automatically via GitHub workflow or manually.
- When more info is needed, apply `needs-info` and ask the reporter.
- Once all info is gathered, move to `ready-for-agent` or `ready-for-human` depending on who will act.
- `wontfix` is terminal — close the issue with a comment explaining why.
