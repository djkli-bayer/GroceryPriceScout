// Generates the PWA PNG icons (green tile with a white ring) without any image dependencies.
import { deflateSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'

const GREEN = [0x2e, 0x7d, 0x32]
const WHITE = [0xff, 0xff, 0xff]
const PAGE = [0xf4, 0xf6, 0xf4]

function crc32(buf) {
  let c, crc = 0xffffffff
  for (let n = 0; n < buf.length; n++) {
    c = (crc ^ buf[n]) & 0xff
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    crc = (crc >>> 8) ^ c
  }
  return (crc ^ 0xffffffff) >>> 0
}

function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length)
  const body = Buffer.concat([Buffer.from(type), data])
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(body))
  return Buffer.concat([len, body, crc])
}

// ringFraction: ring outer radius as a fraction of the size; cornerFraction: rounded-corner radius (0 = full bleed).
function png(size, ringFraction, cornerFraction) {
  const raw = Buffer.alloc((size * 3 + 1) * size)
  const c = (size - 1) / 2
  const outer = size * ringFraction
  const inner = outer * 0.45
  const corner = size * cornerFraction
  let o = 0
  for (let y = 0; y < size; y++) {
    raw[o++] = 0
    for (let x = 0; x < size; x++) {
      let color = GREEN
      const d = Math.hypot(x - c, y - c)
      if (d <= outer && d > inner) color = WHITE
      if (corner > 0) {
        const cx = Math.min(x, size - 1 - x), cy = Math.min(y, size - 1 - y)
        if (cx < corner && cy < corner && Math.hypot(corner - cx, corner - cy) > corner) color = PAGE
      }
      raw[o++] = color[0]; raw[o++] = color[1]; raw[o++] = color[2]
    }
  }
  const ihdr = Buffer.alloc(13)
  ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4)
  ihdr[8] = 8; ihdr[9] = 2 // 8-bit RGB
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0)),
  ])
}

mkdirSync('public', { recursive: true })
writeFileSync('public/icon-192.png', png(192, 0.28, 0.22))
writeFileSync('public/icon-512.png', png(512, 0.28, 0.22))
writeFileSync('public/apple-touch-icon.png', png(180, 0.28, 0)) // iOS rounds the corners itself
// Maskable icons need the artwork inside the central 80% safe zone and a full-bleed background.
writeFileSync('public/icon-maskable-512.png', png(512, 0.2, 0))
console.log('icons written')
