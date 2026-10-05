import { getPersona, PERSONAS } from "./personas";

test("analyse:claims makes a Copywriter", () => {
  expect(getPersona({ permissions: ["analyse:claims"] })).toBe(PERSONAS.COPYWRITER);
  expect(PERSONAS.COPYWRITER.label).toBe("Copywriter");
});

test("publish:product makes a Senior Editor, with or without analyse:claims", () => {
  expect(getPersona({ permissions: ["analyse:claims", "publish:product"] })).toBe(PERSONAS.SENIOR_EDITOR);
  expect(getPersona({ permissions: ["publish:product"] })).toBe(PERSONAS.SENIOR_EDITOR);
  expect(PERSONAS.SENIOR_EDITOR.label).toBe("Senior Editor");
});

test.each([undefined, null, {}, { permissions: [] }, { permissions: ["resolve:findings"] }])(
  "anyone else has no access (%p)",
  (user) => {
    expect(getPersona(user)).toBe(PERSONAS.NONE);
  }
);

test("a role name is never enough: only permissions decide", () => {
  expect(getPersona({ role: "compliance-manager", permissions: [] })).toBe(PERSONAS.NONE);
  expect(getPersona({ role: "senior-editor", permissions: ["analyse:claims"] })).toBe(PERSONAS.COPYWRITER);
});
