/**
 * Off-main-thread map decoding. Everything between the file and the upload happens here: the JPEG
 * decode, the downscale, the no-data fill, and reading the result back as bytes. On the main thread
 * each of those is a stall; a 16K map was seconds of frozen frame.
 */
import { decodePixels, DecodeJob } from './mapDecode'

// One job at a time: the main thread only sends the next once this one has answered.
self.onmessage = async (e: MessageEvent<{ job: DecodeJob }>) => {
  try {
    const pixels = await decodePixels(e.data.job)
    self.postMessage({ pixels }, { transfer: [pixels.data.buffer] })
  } catch (err) {
    self.postMessage({ error: String(err) })
  }
}
