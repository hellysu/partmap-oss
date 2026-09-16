# Releasing PartMap

1. Ensure `main` is clean and all hosted pull-request checks pass.
2. Run `./verify-development.ps1 -Publish` on a trusted Windows development machine.
3. Review release notes and confirm no private paths, product names, or local data are included.
4. Create a `v*` tag only from a reviewed commit.
5. The release workflow builds the installer and portable packages on the trusted release runner and publishes checksums with the GitHub Release.

Never configure public pull requests to execute arbitrary code on the self-hosted release runner.
