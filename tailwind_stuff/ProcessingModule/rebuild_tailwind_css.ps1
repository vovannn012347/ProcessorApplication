Write-Host "--- Rebuilding Processor Module CSS ---" -ForegroundColor Cyan

# We explicitly point to the local config to ensure 'pm-' prefixes are used
npx tailwindcss -i ./a_wwwroot_Styles/input.css -o ./wwwroot/css/processor.min.css -c ./tailwind.config.js 
# --minify

if ($LASTEXITCODE -eq 0) {
    Write-Host "Success! Output: ./wwwroot/css/processor.min.css" -ForegroundColor Green
} else {
    Write-Host "Error: Tailwind build failed. Check the input.css path or config file." -ForegroundColor Red
}