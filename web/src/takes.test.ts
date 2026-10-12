import { describe, expect, it } from 'vitest';
import { splice, toWav, RATE } from './takes';

const f = (...x: number[]) => new Float32Array(x);
const arr = (a: Float32Array) => Array.from(a);

describe('splice', () => {
  it('adds a take at the end, wherever the playback position is', () => {
    expect(arr(splice(f(1, 2, 3), f(9, 9), 1, 'end'))).toEqual([1, 2, 3, 9, 9]);
  });
  it('puts a take in at the position and moves the rest along', () => {
    expect(arr(splice(f(1, 2, 3), f(9, 9), 1, 'insert'))).toEqual([1, 9, 9, 2, 3]);
  });
  it('records over from the position for as long as the take lasts', () => {
    expect(arr(splice(f(1, 2, 3, 4), f(9, 9), 1, 'overwrite'))).toEqual([1, 9, 9, 4]);
  });
  it('recording over near the end makes the track longer', () => {
    expect(arr(splice(f(1, 2, 3), f(9, 9, 9), 2, 'overwrite'))).toEqual([1, 2, 9, 9, 9]);
  });
  it('keeps the position inside the track', () => {
    expect(arr(splice(f(1, 2), f(9), 50, 'insert'))).toEqual([1, 2, 9]);
    expect(arr(splice(f(1, 2), f(9), -5, 'insert'))).toEqual([9, 1, 2]);
  });
  it('starts a track from nothing', () => {
    expect(arr(splice(f(), f(5, 6), 0, 'overwrite'))).toEqual([5, 6]);
  });
});

describe('toWav', () => {
  it('writes a 16 kHz mono 16-bit WAV with two bytes per sample', async () => {
    const wav = toWav(f(0, 1, -1));
    expect(wav.size).toBe(44 + 6);
    const v = new DataView(await wav.arrayBuffer());
    expect(String.fromCharCode(v.getUint8(0), v.getUint8(1), v.getUint8(2), v.getUint8(3))).toBe('RIFF');
    expect(v.getUint32(24, true)).toBe(RATE);
    expect(v.getUint16(22, true)).toBe(1);
    expect(v.getInt16(46, true)).toBe(0x7fff);
    expect(v.getInt16(48, true)).toBe(-0x8000);
  });
});

describe('cut and peaks', () => {
  it('cuts a stretch out', async () => {
    const { cut } = await import('./takes');
    expect(Array.from(cut(new Float32Array([1, 2, 3, 4, 5]), 1, 3))).toEqual([1, 4, 5]);
  });
  it('finds the loudest point per column', async () => {
    const { peaks } = await import('./takes');
    expect(Array.from(peaks(new Float32Array([0.1, -0.5, 0.2, 0.3]), 2))).toEqual([0.5, 0.30000001192092896]);
  });
});
