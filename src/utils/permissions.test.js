import { hasPermission } from "./permissions";

describe("hasPermission", () => {
  test("is true when the user holds the permission", () => {
    const user = { permissions: ["analyse:claims", "publish:product"] };
    expect(hasPermission(user, "publish:product")).toBe(true);
  });

  test("is false when the user lacks the permission", () => {
    const user = { permissions: ["analyse:claims"] };
    expect(hasPermission(user, "publish:product")).toBe(false);
  });

  test.each([undefined, null, {}, { permissions: null }, { permissions: [] }])(
    "fails closed for a missing user or permission list (%p)",
    (user) => {
      expect(hasPermission(user, "publish:product")).toBe(false);
    }
  );

  test("does not treat a role name as a permission", () => {
    const user = { role: "compliance-manager", permissions: [] };
    expect(hasPermission(user, "publish:product")).toBe(false);
  });
});
