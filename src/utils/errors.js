// picks the message to show when a request fails. 403 means not allowed, not broken. 401 means sign-in ended, and sends them back to log in
export const describeError = (error) => {
  if (error?.status === 401) return "Your sign-in has ended. Please sign in again.";
  if (error?.status === 403) return error.message || "You do not have access to this.";
  return error?.message || "Something went wrong. Please try again.";
};
