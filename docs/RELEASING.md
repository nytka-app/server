# Releasing

[release-please](https://github.com/googleapis/release-please) reads conventional commits on
`main` and keeps a release PR up to date. Merging that PR cuts the release.

## Patch by default

Before 1.0 (`bump-minor-pre-major: true`, `bump-patch-for-minor-pre-major: true` in
`release-please-config.json`):

- a `fix:` commit bumps the patch version (0.N.x);
- a `feat:` commit also bumps the patch version, not the minor — features ship continuously as
  patch releases through the release-please PR, same as today;
- a commit with a `BREAKING CHANGE:` footer still bumps the minor (0.N.0), because a breaking
  change needs its own version even before 1.0.

## Cutting a minor by hand

A minor version marks a milestone closing — a named goal on the team's Plane board, not a
count of features or commits. To cut one:

1. Confirm the milestone's Plane issue is done.
2. On `main`, add an empty commit whose message ends with a `Release-As` footer naming the next
   minor version:

   ```bash
   git commit --allow-empty -m "chore: release 0.<N+1>.0 for <milestone name>" -m "Release-As: 0.<N+1>.0"
   ```

   release-please reads the footer and sets the next release PR to that version instead of the
   usual patch bump.
3. Review and merge the release PR as usual.
