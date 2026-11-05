type PathPart = string | number;

type Span = { start: number; end: number };
type Member = { key: string; keyStart: number; value: Span };

class Scanner {
  private readonly source: string;
  private index: number;

  constructor(source: string, index = 0) {
    this.source = source;
    this.index = index;
  }

  parseRoot(): Span {
    try { JSON.parse(this.source); }
    catch { throw new Error("JSON非法"); }
    this.ws();
    const span = this.value();
    this.ws();
    if (this.index !== this.source.length) throw new Error("JSON后有多余内容");
    return span;
  }

  parsePath(path: readonly PathPart[]): Span {
    let span = this.parseRoot();
    for (const part of path) {
      this.index = span.start;
      this.ws();
      if (typeof part === "string") {
        const members = this.objectMembers();
        const found = members.filter((member) => member.key === part);
        if (found.length !== 1) throw new Error("路径不存在或对象键重复");
        span = found[0].value;
      } else {
        const values = this.arrayValues();
        if (!Number.isInteger(part) || part < 0 || part >= values.length) throw new Error("路径不存在或类型不匹配");
        span = values[part];
      }
    }
    return span;
  }

  objectMembersAtRoot(): { span: Span; members: Member[] } {
    const span = this.parseRoot();
    this.index = span.start;
    this.ws();
    if (this.peek() !== "{") throw new Error("根不是对象");
    return { span, members: this.objectMembers() };
  }

  arrayValuesAtRoot(): Span[] {
    const span = this.parseRoot();
    this.index = span.start;
    this.ws();
    if (this.peek() !== "[") throw new Error("根不是数组");
    return this.arrayValues();
  }

  private value(): Span {
    this.ws();
    const start = this.index;
    const char = this.peek();
    if (char === '"') this.string();
    else if (char === "{") this.objectMembers();
    else if (char === "[") this.arrayValues();
    else if (char === "t") this.literal("true");
    else if (char === "f") this.literal("false");
    else if (char === "n") this.literal("null");
    else this.number();
    return { start, end: this.index };
  }

  private objectMembers(): Member[] {
    if (this.peek() !== "{") throw new Error("路径类型不匹配");
    this.index++;
    this.ws();
    const members: Member[] = [];
    const keys = new Set<string>();
    if (this.peek() === "}") { this.index++; return members; }
    while (true) {
      this.ws();
      const keyStart = this.index;
      const key = this.stringValue();
      this.ws();
      if (this.peek() !== ":") throw new Error("对象缺少冒号");
      this.index++;
      const value = this.value();
      if (keys.has(key)) throw new Error("对象键重复");
      keys.add(key);
      members.push({ key, keyStart, value });
      this.ws();
      if (this.peek() === "}") { this.index++; return members; }
      if (this.peek() !== ",") throw new Error("对象缺少逗号");
      this.index++;
    }
  }

  private arrayValues(): Span[] {
    if (this.peek() !== "[") throw new Error("路径类型不匹配");
    this.index++;
    this.ws();
    const values: Span[] = [];
    if (this.peek() === "]") { this.index++; return values; }
    while (true) {
      values.push(this.value());
      this.ws();
      if (this.peek() === "]") { this.index++; return values; }
      if (this.peek() !== ",") throw new Error("数组缺少逗号");
      this.index++;
    }
  }

  private stringValue(): string {
    const start = this.index;
    this.string();
    try { return JSON.parse(this.source.slice(start, this.index)) as string; }
    catch { throw new Error("字符串非法"); }
  }

  private string(): void {
    if (this.source[this.index++] !== '"') throw new Error("需要字符串");
    while (this.index < this.source.length) {
      const char = this.source[this.index++];
      if (char === '"') return;
      if (char === "\\") {
        if (this.index >= this.source.length) throw new Error("字符串未结束");
        const escaped = this.source[this.index++];
        if (escaped === "u") {
          if (!/^[0-9a-fA-F]{4}$/.test(this.source.slice(this.index, this.index + 4))) throw new Error("字符串转义非法");
          this.index += 4;
        } else if (!'"\\/bfnrt'.includes(escaped)) throw new Error("字符串转义非法");
      } else if (char < " ") throw new Error("字符串含控制字符");
    }
    throw new Error("字符串未结束");
  }

  private number(): void {
    const rest = this.source.slice(this.index);
    const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(rest);
    if (!match) throw new Error("值非法");
    this.index += match[0].length;
  }

  private literal(literal: string): void {
    if (this.source.slice(this.index, this.index + literal.length) !== literal) throw new Error("值非法");
    this.index += literal.length;
  }

  private ws(): void { while (this.source[this.index] === " " || this.source[this.index] === "\t" || this.source[this.index] === "\r" || this.source[this.index] === "\n") this.index++; }
  private peek(): string { return this.source[this.index] ?? ""; }
}

function token(source: string, span: Span): string { return source.slice(span.start, span.end); }

export function extractJsonValue(source: string, path: readonly PathPart[]): string {
  const scanner = new Scanner(source);
  return token(source, scanner.parsePath(path));
}

export function replaceJsonObjectProperties(source: string, replacements: Readonly<Record<string, string>>): string {
  const scanner = new Scanner(source);
  const { members } = scanner.objectMembersAtRoot();
  for (const replacement of Object.values(replacements)) {
    new Scanner(replacement).parseRoot();
  }
  const edits = members.filter((member) => Object.prototype.hasOwnProperty.call(replacements, member.key))
    .map((member) => ({ start: member.value.start, end: member.value.end, value: replacements[member.key] }));
  const missing = Object.keys(replacements).filter((key) => !members.some((member) => member.key === key));
  let result = source;
  for (const edit of edits.sort((a, b) => b.start - a.start)) result = result.slice(0, edit.start) + edit.value + result.slice(edit.end);
  if (missing.length) {
    const insertion = missing.map((key) => `${JSON.stringify(key)}:${replacements[key]}`).join(",");
    const close = result.lastIndexOf("}");
    let insertionPoint = close;
    while (insertionPoint > 0 && /\s/.test(result[insertionPoint - 1])) insertionPoint--;
    const hasMembers = members.length > 0;
    result = result.slice(0, insertionPoint) + (hasMembers ? "," : "") + insertion + result.slice(insertionPoint);
  }
  return result;
}

export function splitJsonArrayValues(source: string): string[] {
  const scanner = new Scanner(source);
  const spans = scanner.arrayValuesAtRoot();
  return spans.map((span) => token(source, span));
}
