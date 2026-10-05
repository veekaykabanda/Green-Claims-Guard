import { hasPermission } from "./permissions";

// label comes from the token's permissions, same check the server uses, so it never disagrees with what you can actually do
export const PERSONAS = {
  COPYWRITER: { id: "copywriter", label: "Copywriter" },
  SENIOR_EDITOR: { id: "senior-editor", label: "Senior Editor" },
  NONE: { id: "none", label: "No access" },
};

export const getPersona = (user) => {
  if (hasPermission(user, "publish:product")) return PERSONAS.SENIOR_EDITOR;
  if (hasPermission(user, "analyse:claims")) return PERSONAS.COPYWRITER;
  return PERSONAS.NONE;
};
