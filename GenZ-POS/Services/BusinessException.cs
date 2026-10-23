namespace GenZPOS.Services;

/// <summary>
/// A business-rule failure whose message is written for the shop user ("Only 3 in stock", "Select a supplier").
/// <see cref="Components.PageBase"/> shows these messages as-is; other exceptions get a friendly generic message.
/// </summary>
public class BusinessException(string message) : Exception(message);
