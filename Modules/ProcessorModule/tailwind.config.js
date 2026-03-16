/** @type {import('tailwindcss').Config} */
module.exports = {
    prefix: 'pm-',
    content: [
        './Views/**/*.cshtml'   // Scans all .cshtml files in the Views folder
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