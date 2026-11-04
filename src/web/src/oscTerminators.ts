/**
 * Ghostty answers an OSC request with the terminator the request came with, BEL or ST, and xterm.js does not tell its
 * handlers which one it was. This reads the output before xterm.js does and queues, in order, the terminators of the
 * requests of one OSC number, so its handler can take the one its own request ended with.
 */
export class OscTerminators {
  /** xterm.js's OSC payload limit: a longer request never reaches the handler, so it leaves nothing in the queue. */
  private static readonly payloadLimit = 10_000_000;

  private readonly ident: string;
  private readonly queue: boolean[] = [];
  private state: "ground" | "escape" | "ident" | "body" = "ground";
  private number = "";
  private matches = false;
  private length = 0;

  constructor(ident: number) {
    this.ident = String(ident);
  }

  /** Reads one chunk of output, in the order xterm.js gets them. */
  scan(data: string): void {
    for (let i = 0; i < data.length; i++) {
      const code = data.charCodeAt(i);
      switch (this.state) {
        case "ground":
          if (code === 0x1b) {
            this.state = "escape";
          } else if (code === 0x9d) {
            this.begin();
          }
          break;
        case "escape":
          if (code === 0x5d) {
            this.begin();
          } else if (code !== 0x1b) {
            this.state = "ground";
          }
          break;
        case "ident":
        case "body":
          this.step(code);
          break;
      }
    }
  }

  /** Whether the oldest request not yet taken ended with BEL; false (ST) when none is queued. */
  take(): boolean {
    return this.queue.shift() ?? false;
  }

  private begin(): void {
    this.state = "ident";
    this.number = "";
    this.matches = false;
    this.length = 0;
  }

  private step(code: number): void {
    if (code === 0x07) {
      this.end(true);
    } else if (code === 0x9c) {
      this.end(false);
    } else if (code === 0x1b) {
      // ESC ends the string as xterm.js ends it; the backslash of ST, if it follows, is read as an escape.
      this.end(false);
      this.state = "escape";
    } else if (code === 0x18 || code === 0x1a) {
      this.state = "ground";
    } else if (code < 0x20) {
      // C0 controls inside the string are ignored, as xterm.js ignores them.
    } else if (this.state === "ident") {
      if (code >= 0x30 && code <= 0x39) {
        this.number += String.fromCharCode(code);
      } else {
        this.matches = code === 0x3b && this.number === this.ident;
        this.state = "body";
      }
    } else {
      this.length++;
    }
  }

  private end(bel: boolean): void {
    if ((this.state === "body" ? this.matches : this.number === this.ident) && this.length <= OscTerminators.payloadLimit) {
      this.queue.push(bel);
    }

    this.state = "ground";
  }
}
