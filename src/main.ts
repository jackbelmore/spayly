import { App } from './app/app'

const stage = document.getElementById('stage')
const labels = document.getElementById('labels')
if (!stage || !labels) throw new Error('missing #stage or #labels')

const app = new App(stage, labels)
// Handy for poking at the simulation from the console.
;(window as unknown as { spayly: App }).spayly = app
