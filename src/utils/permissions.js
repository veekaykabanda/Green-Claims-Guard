// checks the real permissions list from Auth0, use this instead of user.role for anything security related
export const hasPermission = (user, permission) => {
  const permissions = user?.permissions || [];
  return permissions.includes(permission);
};
