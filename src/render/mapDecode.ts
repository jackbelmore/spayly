import { applyTint, fillNoData } from './gapFill'

/**
 * Turning a map file into pixels the GPU can take. Runs in the texture worker; the same code runs on
 * the main thread only if the worker cannot start.
 */

/**
 * Options for a bitmap handed straight to three.js, the fallback when a map cannot be read back as
 * pixels. three ignores `texture.flipY` for bitmaps and browsers ignore the unpack flip for them, so
 * the flip is baked in; colour conversion and premultiplication are off to match what three does
 * for an image element. In Chromium each of these costs a pass over the whole image on the calling
 * thread, which is why the normal path does all of this in the worker instead.
 */
export const BITMAP_FOR_GPU: ImageBitmapOptions = { imageOrientation: 'flipY', premultiplyAlpha: 'none', colorSpaceConversion: 'none' }

export interface DecodeJob {
  blob: Blob
  /** Size to decode at; smaller than the source when streaming asked for less. */
  width: number
  height: number
  resize: boolean
  /** Longitude shift, in turns. */
  roll: number
  /** Grow imaged terrain over black no-data regions. */
  fillGaps: boolean
  /** Multiply by this colour, as '#rrggbb'. */
  tint?: string
}

/** RGBA bytes, bottom row first: the order WebGL wants them in, so the upload needs no flip. */
export interface Pixels {
  data: Uint8ClampedArray
  width: number
  height: number
}

/**
 * Decode a map, apply any roll, fill and tint, and read it back as bytes. The canvas draws it upside
 * down, which is what makes the rows come out bottom first. The fill and tint do not care: both are
 * symmetric in latitude apart from the fill's noise, which is only noise.
 */
export async function decodePixels(job: DecodeJob): Promise<Pixels> {
  const { width: w, height: h } = job
  // No premultiplyAlpha option: the canvas premultiplies whatever it is given, and each option is a
  // pass over the full-size image.
  const src = await createImageBitmap(job.blob, {
    colorSpaceConversion: 'none',
    ...(job.resize ? { resizeWidth: w, resizeHeight: h, resizeQuality: 'high' as const } : {}),
  })
  try {
    const canvas = new OffscreenCanvas(w, h)
    const cx = canvas.getContext('2d', { willReadFrequently: true })
    if (!cx) throw new Error(`no 2d context at ${w}x${h}`)
    cx.setTransform(1, 0, 0, -1, 0, h)
    const off = job.roll ? Math.round((((job.roll % 1) + 1) % 1) * w) : 0
    cx.drawImage(src, off, 0, w, h)
    if (off) cx.drawImage(src, off - w, 0, w, h)
    const img = cx.getImageData(0, 0, w, h)
    if (job.fillGaps) fillNoData(img.data, w, h)
    if (job.tint) applyTint(img.data, job.tint)
    return { data: img.data, width: w, height: h }
  } finally {
    src.close()
  }
}
