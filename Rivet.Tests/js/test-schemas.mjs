/**
 * Zod 4 smoke test for JSON Schema output.
 * Usage: node test-schemas.mjs <path-to-schemas.ts>
 *
 * Reads the generated schemas.ts, strips TypeScript syntax to get evaluable JS,
 * then validates objects against schemas using fromJSONSchema().
 */

// TODO: format-level validation (uuid, date-time, etc.)

import { readFileSync } from "node:fs";
import { fromJSONSchema } from "zod";

const schemasPath = process.argv[2];
if (!schemasPath) {
  console.error("Usage: node test-schemas.mjs <path-to-schemas.ts>");
  process.exit(1);
}

// Read and strip TS-only syntax to get evaluable JS
let code = readFileSync(schemasPath, "utf-8");

// Remove TS-only syntax to get evaluable JS
code = code.replace(/^import type .*;\n/gm, "");
code = code.replace(/^type .*;\n/gm, "");
code = code.replace(/: Record<string, JSONSchema>/g, "");
code = code.replace(/: JSONSchema/g, "");
code = code.replaceAll("export ", "");

// Evaluate in a function scope to capture the variables
const fn = new Function(`
  ${code}
  return { UserDtoSchema, AddressDtoSchema };
`);

const { UserDtoSchema, AddressDtoSchema } = fn();

// Create Zod schemas from JSON Schema
const userSchema = fromJSONSchema(UserDtoSchema);
const addressSchema = fromJSONSchema(AddressDtoSchema);

// Valid data should pass
const validAddress = { street: "123 Main St", city: "Springfield" };
const validUser = { name: "Alice", age: 30, address: validAddress };

const addressResult = addressSchema.safeParse(validAddress);
if (!addressResult.success) {
  console.error("Valid address failed validation:", addressResult.error.issues);
  process.exit(1);
}

const userResult = userSchema.safeParse(validUser);
if (!userResult.success) {
  console.error("Valid user failed validation:", userResult.error.issues);
  process.exit(1);
}

// Invalid data should fail
const invalidUser = { name: 123, age: "not a number" };
const invalidResult = userSchema.safeParse(invalidUser);
if (invalidResult.success) {
  console.error("Invalid user should have failed validation but passed");
  process.exit(1);
}

console.log("All Zod integration tests passed.");
