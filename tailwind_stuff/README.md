# tailwind_stuff (reference only, not built or loaded)

The UI no longer uses Tailwind (2026-11-13): every page uses `ProcessorApplication/wwwroot/css/ui.css` (plain CSS, colour roles
as variables at the top) plus Font Awesome. These are the former Tailwind sources and outputs, kept for reference, one folder
per project with the same relative paths as before:

- `<Project>/tailwind.config.js` - config with the class prefix (`mm-` main, `pm-` Processing, `p2p-`, `ftt-` FileTransfer,
  `plss-` Providerless, `spv-` ScriptProvisioning)
- `<Project>/a_wwwroot_Styles/input.css` - Tailwind input (+ hand-written rules; the main app's sidebar/spinner/font rules now
  live in ui.css)
- `<Project>/postcss.config.js`, `<Project>/rebuild_tailwind_css.ps1` - build setup
- `<Project>/wwwroot/css/*.css` - the last built outputs
- `ProcessorApplication/package.json` + `package-lock.json` - npm dev dependencies (tailwindcss, postcss, autoprefixer)

To rebuild one for comparison: copy the project's files back next to its .csproj, `npm install`, run its rebuild script.
