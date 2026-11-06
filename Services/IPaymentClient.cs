using OrderIntakeApi.Models;

namespace OrderIntakeApi.Services;

public interface IPaymentClient
{
    Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken cancellationToken = default);
}
