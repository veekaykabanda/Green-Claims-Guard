import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import DataTransferView from "./DataTransferView";

const renderView = (props = {}) =>
  render(
    <DataTransferView
      runSeedImport={() => {}}
      isSeeding={false}
      seedResult={null}
      exportAuditCsv={() => {}}
      isExportingAudit={false}
      auditExportError={null}
      {...props}
    />
  );

test("Download CSV starts the export instead of saying it is coming soon", async () => {
  const alert = jest.spyOn(window, "alert").mockImplementation(() => {});
  const exportAuditCsv = jest.fn();
  renderView({ exportAuditCsv });

  await userEvent.click(screen.getByRole("button", { name: "Download CSV" }));

  expect(exportAuditCsv).toHaveBeenCalledTimes(1);
  expect(alert).not.toHaveBeenCalled();
  alert.mockRestore();
});

test("while the file is being prepared the button says so and cannot be pressed twice", () => {
  renderView({ isExportingAudit: true });
  expect(screen.getByRole("button", { name: "Preparing..." })).toBeDisabled();
});

test("a failed export shows the reason", () => {
  renderView({ auditExportError: "Only a Senior Editor can export the audit trail." });
  expect(screen.getByText("Only a Senior Editor can export the audit trail.")).toBeInTheDocument();
});
