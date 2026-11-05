import { describe, expect, it } from "vitest";
import { extractJsonValue, replaceJsonObjectProperties, splitJsonArrayValues } from "./raw-json";

describe("raw JSON", () => {
  it("保留数字和字符串内部原文，并支持数组路径", () => {
    const source = '{"a":[{"n":12.0,"s":"逗号,括号[] \\"x\\""}],"x":true}';
    expect(extractJsonValue(source, ["a", 0, "n"])).toBe("12.0");
    expect(extractJsonValue(source, ["a", 0, "s"])).toBe('"逗号,括号[] \\"x\\""');
  });

  it("替换、追加且不改未知 raw 数字", () => {
    const source = '{ "keep" : 320.0, "x": 1e+02 }';
    expect(replaceJsonObjectProperties(source, { x: "false", added: "[1, 2]" })).toBe('{ "keep" : 320.0, "x": false,"added":[1, 2] }');
  });

  it("拆分嵌套数组元素", () => {
    expect(splitJsonArrayValues('[1, {"x": "a,b"}, [true, null], -2.5e-1]')).toEqual(["1", '{"x": "a,b"}', "[true, null]", "-2.5e-1"]);
  });

  it("拒绝非法 JSON、重复 key 和错误路径", () => {
    expect(() => splitJsonArrayValues("[1,]")).toThrow();
    expect(() => splitJsonArrayValues('["\\q"]')).toThrow("JSON非法");
    expect(() => splitJsonArrayValues('["\\uZZZZ"]')).toThrow("JSON非法");
    expect(() => extractJsonValue('{"a":1,"a":2}', ["a"])).toThrow("对象键重复");
    expect(() => extractJsonValue('{"a":1}', ["a", 0])).toThrow();
    expect(() => replaceJsonObjectProperties("{}", { x: "{bad" })).toThrow();
    expect(() => replaceJsonObjectProperties("{}", { x: '"\\q"' })).toThrow("JSON非法");
    expect(() => splitJsonArrayValues("[\u00a0 1]")).toThrow("JSON非法");
  });
});
