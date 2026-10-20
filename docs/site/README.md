# Project website

The public project page lives at `https://akreao.github.io/ro3emu-server/`.
Only this directory is deployed by `.github/workflows/pages.yml`; server files,
exported client data and local build output are not part of the Pages artifact.

Preview with any static HTTP server and run `node tools/check-site.mjs` from the
repository root before submitting changes. The page needs no package install or
build step. All content and navigation work without JavaScript; JavaScript adds
only a copy button.

Keep the canonical URL, Open Graph/Twitter URLs, JSON-LD and `sitemap.xml` aligned
if the project URL changes. The sitemap can be submitted by a verified site owner
in Google Search Console. A `robots.txt` inside a project subdirectory does not
control the host, so this site does not pretend to provide one.

The social preview is original text/vector artwork, with no game images. On
Windows, regenerate it with `pwsh -File tools/render-social-preview.ps1`. GitHub's
repository social preview is a separate repository setting; use the same PNG
there so links to the code and project page share a visual identity.

The page describes implemented source and directs visitors to the current README
for scope. Do not add live-player counters, performance figures, release badges,
gameplay screenshots or compatibility promises without corresponding evidence.
