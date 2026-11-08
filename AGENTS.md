# Repository workflow

- Work and build locally. After verifying each completed project change, commit the intended source changes and push to `origin/main`. Never force-push or include unrelated working-tree changes.
- Keep compiled executables, installers, driver downloads, and build output out of Git. Build release installers locally with `build.ps1` and attach only the versioned setup executables to the matching GitHub Release.
- Do not add a GitHub Actions workflow to build or publish installers.
