# LZMA SDK (C#)

The LZMA encoder and decoder from Igor Pavlov's [LZMA SDK](https://www.7-zip.org/sdk.html) 25.01, `CS\7zip`,
unchanged. The SDK is in the public domain. `Bundle` compresses the app with it at build time and the
starter decompresses it; the encoder isn't reachable from the starter, so Native AOT leaves it out.
