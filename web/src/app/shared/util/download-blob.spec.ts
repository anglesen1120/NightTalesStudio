import type { Mock } from 'vitest';
import { downloadBlob } from './download-blob';
import { safeFilename } from './safe-filename';

describe('downloadBlob', () => {
  const blob = new Blob(['x']);
  let createObjectURL: Mock<(blob: Blob) => string>;
  let revokeObjectURL: Mock<(url: string) => void>;
  let clicked: HTMLAnchorElement[];

  beforeEach(() => {
    clicked = [];
    // jsdom implements neither function: install fakes on the real URL object and restore afterwards.
    createObjectURL = vi.fn(() => 'blob:fake-url');
    revokeObjectURL = vi.fn();
    Object.defineProperty(URL, 'createObjectURL', { value: createObjectURL, configurable: true, writable: true });
    Object.defineProperty(URL, 'revokeObjectURL', { value: revokeObjectURL, configurable: true, writable: true });
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this);
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    Reflect.deleteProperty(URL, 'createObjectURL');
    Reflect.deleteProperty(URL, 'revokeObjectURL');
  });

  it('sets a sanitised download name, clicks once, and revokes the object URL', () => {
    downloadBlob(blob, '../"evil".mp4');

    expect(createObjectURL).toHaveBeenCalledExactlyOnceWith(blob);
    expect(clicked).toHaveLength(1);
    expect(clicked[0]?.download).toBe(safeFilename('../"evil".mp4'));
    expect(clicked[0]?.download).toBe('evil.mp4');
    expect(clicked[0]?.getAttribute('href')).toBe('blob:fake-url');
    expect(revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:fake-url');
  });

  it('falls back to "download" for a name with nothing usable', () => {
    downloadBlob(blob, '...');
    expect(clicked[0]?.download).toBe('download');
  });

  it('still revokes the object URL when the click throws (finally)', () => {
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {
      throw new Error('click blocked');
    });
    expect(() => downloadBlob(blob, 'a.mp4')).toThrow('click blocked');
    expect(revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:fake-url');
  });

  it('does not leave an anchor in the document', () => {
    const before = document.body.querySelectorAll('a').length;
    downloadBlob(blob, 'a.mp4');
    expect(document.body.querySelectorAll('a').length).toBe(before);
  });
});
