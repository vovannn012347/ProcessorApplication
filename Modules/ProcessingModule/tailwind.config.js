/** @type {import('tailwindcss').Config} */
module.exports = {
    prefix: 'pm-',
    corePlugins: {
        preflight: false,
    },
    content: [
        './Views/**/*.cshtml',   // Scans all .cshtml files in the Views folder
        './wwwroot/js/**/*.js'   // Scans all .javascript stuffs
    ],
    theme: {
        extend: {
            fontFamily: {
                sans: ['Inter', 'sans-serif'], // This uses the local font you're about to add
            },
        },
    },
    plugins: [],
}