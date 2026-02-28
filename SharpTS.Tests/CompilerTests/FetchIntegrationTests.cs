// All tests migrated to SharedTests/FetchTests.cs for unified interpreter+compiler execution.
// Migrated tests:
//   FetchJson_Compiled / FetchJson_Interpreted → FetchJson_ParsesResponse
//   FetchText_Compiled / FetchText_Interpreted → FetchText_ReturnsBody
//   FetchPost_Compiled / FetchPost_Interpreted → FetchPost_SendsBody
//   FetchWithCustomHeaders_Compiled / FetchWithCustomHeaders_Interpreted → FetchWithCustomHeaders_SendsHeaders
//   FetchResponseStatus_Compiled / FetchResponseStatus_Interpreted → (already covered by FetchResponse_StatusText_ReturnsText)
//   FetchArrayBuffer_Compiled / FetchArrayBuffer_Interpreted → FetchArrayBuffer_ReturnsBinary
//   FetchResponseHeaders_Compiled / FetchResponseHeaders_Interpreted → FetchResponseHeaders_TypeofIsObject
//   FetchPutMethod_Compiled → FetchPutMethod_SendsCorrectMethod
//   FetchDeleteMethod_Compiled → FetchDeleteMethod_SendsCorrectMethod
