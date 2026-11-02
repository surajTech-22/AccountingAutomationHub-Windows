# AAH v4.0.0.577 verified release request

Source merge: `21cf0a06441b71636d7994320b40d51582340404` (PR #73).

Change: retain the original optional VAT / Tax Classification XML labels, clear obsolete VAT/classification values for explicitly GST-marked consolidated destination copies while preserving original branch DR/CR, GSTIN, duty head and source metadata.

PR validation at `7ec90f1a8f949abd551382d22fc7364b6037d2ee`: Windows compile, All Components regression and Consolidated Tally Master Import Contract all passed.

Publish stable release only after BOTH main-branch Windows Compile and All Components Compile And Regression succeed for this **exact** release-request SHA, followed by existing UpdateHost tests, SHA256 manifest validation and GitHub release readback.

Preserve data, no forced process termination, no application files changed until verified graceful shutdown.
