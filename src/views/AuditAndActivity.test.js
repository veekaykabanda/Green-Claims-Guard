import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ApiError } from "../services/api";
import ActivityView from "./ActivityView";
import AuditTrailView, { PAGE_SIZE } from "./AuditTrailView";
import VerifiedFactsView from "./VerifiedFactsView";

const settle = () => act(async () => { await new Promise((resolve) => setTimeout(resolve, 0)); });

const auditRows = (count, offset = 0) =>
  Array.from({ length: count }, (_, i) => ({
    id: offset + i + 1,
    timestamp: "2026-09-25T10:00:00Z",
    userEmail: i === 0 ? "sam@brand.com" : null,
    userId: `auth0|user-${offset + i}`,
    action: i === 0 ? "Publish" : "Submit",
    outcome: "Ok",
    productName: `Product ${offset + i + 1}`,
    justification: null,
    detail: null,
  }));

describe("AuditTrailView", () => {
  const makeApi = (over = {}) => ({
    getAudit: jest.fn(async ({ skip = 0 }) => ({ rows: auditRows(Math.min(PAGE_SIZE, 60 - skip), skip), total: 60 })),
    exportAudit: jest.fn(async () => new Blob(["csv"])),
    ...over,
  });

  beforeEach(() => {
    global.URL.createObjectURL = jest.fn(() => "blob:audit");
    global.URL.revokeObjectURL = jest.fn();
  });

  test("shows the first page in words, with the person's email where known and their id where not", async () => {
    const api = makeApi();
    render(<AuditTrailView api={api} />);
    await settle();

    expect(api.getAudit).toHaveBeenCalledWith({ from: undefined, to: undefined, skip: 0, take: PAGE_SIZE });
    expect(screen.getAllByRole("row")).toHaveLength(PAGE_SIZE + 1);
    expect(screen.getByText("sam@brand.com")).toBeInTheDocument();
    expect(screen.getByText("auth0|user-1")).toBeInTheDocument();
    expect(screen.getByText("Published")).toBeInTheDocument();
    expect(screen.getByText(`Rows 1 to ${PAGE_SIZE} of 60`)).toBeInTheDocument();
  });

  test("pages forward and back, never past either end", async () => {
    const api = makeApi();
    render(<AuditTrailView api={api} />);
    await settle();
    expect(screen.getByRole("button", { name: "Previous" })).toBeDisabled();

    userEvent.click(screen.getByRole("button", { name: "Next" }));
    await settle();
    expect(api.getAudit).toHaveBeenLastCalledWith(expect.objectContaining({ skip: PAGE_SIZE }));
    expect(screen.getByText(`Rows ${PAGE_SIZE + 1} to ${PAGE_SIZE * 2} of 60`)).toBeInTheDocument();

    userEvent.click(screen.getByRole("button", { name: "Next" }));
    await settle();
    expect(screen.getByRole("button", { name: "Next" })).toBeDisabled();
    expect(screen.getByText("Rows 51 to 60 of 60")).toBeInTheDocument();
  });

  test("the dates filter the rows and start again from the first page", async () => {
    const api = makeApi();
    render(<AuditTrailView api={api} />);
    await settle();
    userEvent.click(screen.getByRole("button", { name: "Next" }));
    await settle();

    userEvent.type(screen.getByLabelText("From"), "2026-09-01");
    await settle();
    expect(api.getAudit).toHaveBeenLastCalledWith({ from: "2026-09-01T00:00:00", to: undefined, skip: 0, take: PAGE_SIZE });
  });

  test("the export uses the same dates as the page", async () => {
    const api = makeApi();
    render(<AuditTrailView api={api} />);
    await settle();
    userEvent.type(screen.getByLabelText("From"), "2026-09-01");
    userEvent.type(screen.getByLabelText("To"), "2026-09-30");
    await settle();

    userEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    await settle();

    expect(api.exportAudit).toHaveBeenCalledWith({ from: "2026-09-01T00:00:00", to: "2026-09-30T23:59:59" });
  });

  test("an empty range says so, and a failure can be retried", async () => {
    const empty = makeApi({ getAudit: jest.fn(async () => ({ rows: [], total: 0 })) });
    const { unmount } = render(<AuditTrailView api={empty} />);
    await settle();
    expect(screen.getByText("Nothing in the audit trail for these dates.")).toBeInTheDocument();
    unmount();

    const failing = makeApi({ getAudit: jest.fn(async () => { throw new ApiError(403, { detail: "You do not have access to this." }); }) });
    render(<AuditTrailView api={failing} />);
    await settle();
    expect(screen.getByRole("alert")).toHaveTextContent("You do not have access to this.");
    expect(screen.getByRole("button", { name: "Try again" })).toBeInTheDocument();
  });

  test("a failed export shows why", async () => {
    const api = makeApi({ exportAudit: jest.fn(async () => { throw new ApiError(403, { detail: "You do not have access to this." }); }) });
    render(<AuditTrailView api={api} />);
    await settle();
    userEvent.click(screen.getByRole("button", { name: "Export CSV" }));
    await settle();
    expect(screen.getByRole("alert")).toHaveTextContent("You do not have access to this.");
  });
});

describe("ActivityView", () => {
  const rows = [
    { timestamp: "2026-09-25T10:00:00Z", action: "Submit", outcome: "Ok", productName: "Wrap cardigan", justification: null },
    { timestamp: "2026-09-25T09:00:00Z", action: "Publish", outcome: "Blocked", productName: "Linen dress", justification: null },
    { timestamp: "2026-09-25T08:00:00Z", action: "Keep", outcome: "Ok", productName: "Linen dress", justification: "Certificate on file." },
  ];

  test("shows what the person really did, in words, with their reasons", () => {
    render(<ActivityView activity={{ status: "ok", data: rows, reload: jest.fn() }} />);

    expect(screen.getByText("Submitted for review")).toBeInTheDocument();
    expect(screen.getByText("Published (blocked)")).toBeInTheDocument();
    expect(screen.getByText(/Certificate on file/)).toBeInTheDocument();
    expect(screen.queryByText(/Sustainable Sneakers/)).not.toBeInTheDocument();
  });

  test("says so when there is nothing yet, and offers a retry on failure", () => {
    const { rerender } = render(<ActivityView activity={{ status: "ok", data: [], reload: jest.fn() }} />);
    expect(screen.getByText("You have not done anything yet.")).toBeInTheDocument();

    const reload = jest.fn();
    rerender(<ActivityView activity={{ status: "error", data: null, error: new ApiError(500, null, "The server could not answer."), reload }} />);
    userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(reload).toHaveBeenCalled();
    expect(screen.getByRole("alert")).toHaveTextContent("The server could not answer.");
  });
});

describe("VerifiedFactsView", () => {
  const requests = [
    { id: 1, productId: "p-1", productName: "Wrap cardigan", requestedByEmail: "ada@brand.com", note: "Please add the composition.", createdAt: "2026-09-24T10:00:00Z", hasFacts: false },
    { id: 2, productId: "p-2", productName: "Linen dress", requestedByEmail: null, requestedByUserId: "auth0|w2", note: "Origin missing.", createdAt: "2026-09-23T10:00:00Z", hasFacts: true },
  ];
  const makeApi = () => ({
    getFactsRequests: jest.fn(async () => requests),
    getFacts: jest.fn(async () => ({ status: "NoneOnFile", materials: [], certifications: [], origin: null })),
    saveFacts: jest.fn(),
  });

  test("lists what writers asked for, with who asked and whether facts are on file", async () => {
    render(<VerifiedFactsView api={makeApi()} canEdit />);
    await settle();

    const first = screen.getByText("Wrap cardigan").closest("tr");
    expect(within(first).getByText("ada@brand.com")).toBeInTheDocument();
    expect(within(first).getByText("None yet")).toBeInTheDocument();
    const second = screen.getByText("Linen dress").closest("tr");
    expect(within(second).getByText("auth0|w2")).toBeInTheDocument();
    expect(within(second).getByText("On file")).toBeInTheDocument();
  });

  test("Open goes to that product's facts, and Back returns to the list", async () => {
    const api = makeApi();
    render(<VerifiedFactsView api={api} canEdit />);
    await settle();

    userEvent.click(within(screen.getByText("Wrap cardigan").closest("tr")).getByRole("button", { name: "Open" }));
    await settle();
    expect(api.getFacts).toHaveBeenCalledWith("p-1");
    expect(screen.getByText("Verified facts: Wrap cardigan")).toBeInTheDocument();

    userEvent.click(screen.getByRole("button", { name: "Back" }));
    await settle();
    expect(screen.getByText("Please add the composition.")).toBeInTheDocument();
  });

  test("without the permission the button says View, and the editor is read-only", async () => {
    render(<VerifiedFactsView api={makeApi()} canEdit={false} />);
    await settle();
    userEvent.click(within(screen.getByText("Wrap cardigan").closest("tr")).getByRole("button", { name: "View" }));
    await settle();
    expect(screen.getByText(/don't have permission to change the verified facts/)).toBeInTheDocument();
  });

  test("says so when nobody has asked", async () => {
    const api = { ...makeApi(), getFactsRequests: jest.fn(async () => []) };
    render(<VerifiedFactsView api={api} canEdit />);
    await settle();
    await waitFor(() => expect(screen.getByText("No one has asked for facts.")).toBeInTheDocument());
  });
});
