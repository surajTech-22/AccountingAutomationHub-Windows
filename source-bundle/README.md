# CL017 R14 source bundle

The 18 private Git blobs `part000.b64` … `part017.b64` jointly encode the complete 5,070,732-byte AAH source ZIP (734 members) prepared for GitHub compilation.

Original ZIP SHA256: `c3cd0286e81f43b6b427081cd3313fe08153a86baba032da3c2d8b2b3382571f`.

The Windows Actions workflow reconstructs the ZIP, validates its byte length and SHA256, extracts it into isolated `ci-source/`, and builds the Windows solution. The bundle intentionally contains no live client accounting databases or credentials. This is a transitional source transport format; it is not an automatic application update release.
