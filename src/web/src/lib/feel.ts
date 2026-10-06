/**
 * How difficult a game felt, as guests voted (#130), as a diverging scale: too easy and too hard pull opposite ways
 * from "just right", a neutral gray in the middle. Fixed colours, not the theme's, so the two poles always read as
 * opposites; checked for colour-blind separation and contrast against the dark surfaces (the dataviz validator).
 */
export const FEEL = {
  tooEasy: { label: 'Too easy', color: '#3987e5' },
  justRight: { label: 'Just right', color: '#383835' },
  tooHard: { label: 'Too hard', color: '#e66767' },
} as const
