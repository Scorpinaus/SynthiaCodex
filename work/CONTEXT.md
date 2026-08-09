# work — route change records

One job: hold active and closed records for repository changes.

## Inputs

- Reference: `../CONTEXT.md`
- Reference: `../_system/contracts/change-delivery.md`
- Reference: `../feature_parity.md`

Do NOT load: all records. Load only the selected record and its exact references.

## Process

1. Continue a current record in `active/<change-slug>/`.
2. Read its `CONTEXT.md` and current numbered output.
3. Move the complete record to `closed/` only after the final human gate.
4. Rebuild `_index/records.md` with `../scripts/rebuild-work-index.ps1`.

## Outputs

- Active records in `active/`
- Closed records in `closed/`
- Generated catalog in `_index/records.md`

## Human check

Confirm that the folder state and the record frontmatter show the same status.
