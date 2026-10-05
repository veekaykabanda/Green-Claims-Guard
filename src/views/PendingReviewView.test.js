import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import PendingReviewView from "./PendingReviewView";

const item = (overrides = {}) => ({
  productId: "11111111-1111-1111-1111-111111111111",
  productName: "Wrap cardigan",
  finalDescription: "A soft cardigan in a wool blend.",
  submittedAt: "2026-09-24T10:00:00Z",
  ...overrides,
});

const renderView = (props = {}) =>
  render(
    <PendingReviewView
      pendingReviews={[]}
      isLoadingPendingReviews={false}
      onReview={() => {}}
      {...props}
    />
  );

test("shows an empty state when nothing is waiting", () => {
  renderView();
  expect(screen.getByText(/no reviews awaiting sign-off/i)).toBeInTheDocument();
});

test("shows a loading state instead of the table", () => {
  renderView({ isLoadingPendingReviews: true });
  expect(screen.getByText(/loading pending reviews/i)).toBeInTheDocument();
  expect(screen.queryByRole("table")).not.toBeInTheDocument();
});

test("two products with the same name are both listed and opened by their own id", async () => {
  const onReview = jest.fn();
  const first = item({ productId: "aaaaaaaa-0000-0000-0000-000000000001", finalDescription: "First description." });
  const second = item({ productId: "bbbbbbbb-0000-0000-0000-000000000002", finalDescription: "Second description." });

  renderView({ pendingReviews: [first, second], onReview });

  const rows = screen.getAllByRole("row").slice(1);
  expect(rows).toHaveLength(2);
  rows.forEach((row) => expect(within(row).getByText("Wrap cardigan")).toBeInTheDocument());

  await userEvent.click(within(rows[1]).getByRole("button", { name: /review/i }));
  expect(onReview).toHaveBeenCalledTimes(1);
  expect(onReview).toHaveBeenCalledWith(second);
});

test("names who submitted it: their email if known, otherwise their id, otherwise Unknown", () => {
  renderView({
    pendingReviews: [
      item({ productId: "a", submittedByEmail: "ada@brand.com", submittedByUserId: "auth0|1" }),
      item({ productId: "b", submittedByEmail: null, submittedByUserId: "auth0|2" }),
      item({ productId: "c", submittedByEmail: null, submittedByUserId: null }),
    ],
  });

  expect(screen.getByText("ada@brand.com")).toBeInTheDocument();
  expect(screen.getByText("auth0|2")).toBeInTheDocument();
  expect(screen.getByText("Unknown")).toBeInTheDocument();
  expect(screen.queryByText("auth0|1")).not.toBeInTheDocument();
});

test("shows the market, whether the AI ran, and whether it needs an override", () => {
  renderView({
    pendingReviews: [
      item({ productId: "a", market: "EU", aiStatus: "Ok", needsOverride: false }),
      item({ productId: "b", market: null, aiStatus: "NotConfigured", needsOverride: true }),
    ],
  });

  const [first, second] = screen.getAllByRole("row").slice(1);
  expect(within(first).getByText("EU")).toBeInTheDocument();
  expect(within(first).getByText("Checked")).toBeInTheDocument();
  expect(within(first).queryByText("Needs override")).not.toBeInTheDocument();
  expect(within(second).getByText("UK")).toBeInTheDocument();
  expect(within(second).getByText("Not checked")).toBeInTheDocument();
  expect(within(second).getByText("Needs override")).toBeInTheDocument();
});

test("marks the submissions that are the caller's own, and shows what happened after the last action", () => {
  renderView({ pendingReviews: [item({ isOwnSubmission: true, submittedByEmail: "sam@brand.com" })], message: "Wrap cardigan was published." });

  expect(screen.getByText("Yours")).toBeInTheDocument();
  expect(screen.getByRole("status")).toHaveTextContent("Wrap cardigan was published.");
});
