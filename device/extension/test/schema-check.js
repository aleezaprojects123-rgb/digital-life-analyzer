// A small JSON Schema checker, just big enough for contracts/event.schema.json (no npm packages).
// It supports: $ref (local), type, const, enum, pattern, minLength, maxLength, minimum, maximum, required, properties,
// additionalProperties:false, allOf, anyOf, not, if/then. Anything else in a schema is ignored, except `format: date-time`
// which is checked with Date.parse. test/schema-check.test.js proves it catches bad events.

import { readFileSync } from "node:fs";

export const eventSchema = JSON.parse(readFileSync(new URL("../../../contracts/event.schema.json", import.meta.url), "utf8"));

function typeOf(value) {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  if (Number.isInteger(value)) return "integer";
  return typeof value;
}

function typeMatches(value, wanted) {
  const actual = typeOf(value);
  return actual === wanted || (wanted === "number" && actual === "integer");
}

/** Returns a list of problems (empty = valid). */
export function check(schema, value, root = schema, path = "$") {
  const errors = [];
  const fail = (message) => errors.push(`${path}: ${message}`);

  if (schema.$ref) {
    const target = schema.$ref.replace(/^#\//, "").split("/").reduce((node, key) => node[key], root);
    return check(target, value, root, path);
  }

  if (schema.type !== undefined) {
    const wanted = Array.isArray(schema.type) ? schema.type : [schema.type];
    if (!wanted.some((t) => typeMatches(value, t))) fail(`type ${typeOf(value)} is not ${wanted.join("|")}`);
  }
  if ("const" in schema && JSON.stringify(value) !== JSON.stringify(schema.const)) fail(`must be ${JSON.stringify(schema.const)}`);
  if (schema.enum && !schema.enum.some((e) => JSON.stringify(e) === JSON.stringify(value))) fail(`${JSON.stringify(value)} is not one of ${JSON.stringify(schema.enum)}`);

  if (typeof value === "string") {
    if (schema.pattern && !new RegExp(schema.pattern).test(value)) fail(`does not match ${schema.pattern}`);
    if (schema.minLength !== undefined && value.length < schema.minLength) fail("too short");
    if (schema.maxLength !== undefined && value.length > schema.maxLength) fail("too long");
    if (schema.format === "date-time" && Number.isNaN(Date.parse(value))) fail("not a date-time");
  }
  if (typeof value === "number") {
    if (schema.minimum !== undefined && value < schema.minimum) fail("below minimum");
    if (schema.maximum !== undefined && value > schema.maximum) fail("above maximum");
  }

  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    for (const key of schema.required ?? []) if (!(key in value)) fail(`missing required field ${key}`);
    if (schema.additionalProperties === false) {
      for (const key of Object.keys(value)) if (!(key in (schema.properties ?? {}))) fail(`unexpected field ${key}`);
    }
    for (const [key, sub] of Object.entries(schema.properties ?? {})) {
      if (key in value) errors.push(...check(sub, value[key], root, `${path}.${key}`));
    }
  }

  for (const sub of schema.allOf ?? []) errors.push(...check(sub, value, root, path));
  if (schema.anyOf && !schema.anyOf.some((sub) => check(sub, value, root, path).length === 0)) fail("matches none of anyOf");
  if (schema.not && check(schema.not, value, root, path).length === 0) fail("matches a forbidden pattern (not)");
  if (schema.if && check(schema.if, value, root, path).length === 0 && schema.then) errors.push(...check(schema.then, value, root, path));

  return errors;
}

export const validateEvent = (event) => check(eventSchema, event);
