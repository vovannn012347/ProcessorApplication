/** @type {import('tailwindcss').Config} */
module.exports = {
    prefix: 'mm-',
    content: [
        './Views/**/*.cshtml'   // Scans all .cshtml files in the Views folder
        //'./Pages/**/*.cshtml'  // Scans all .cshtml files in the Pages folder
        // '../Modules/*/**/*.cshtml' //  This one line scans ALL modules
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