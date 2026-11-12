using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using OrderIntakeApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace OrderIntakeApi;

public class SwaggerExampleSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(CreateOrderRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["customerId"] = new OpenApiString("cust-demo-101"),
                ["idempotencyKey"] = new OpenApiString("key-demo-order-101"),
                ["items"] = new OpenApiArray
                {
                    new OpenApiObject
                    {
                        ["sku"] = new OpenApiString("SKU-LAPTOP-PRO"),
                        ["quantity"] = new OpenApiInteger(1),
                        ["unitPrice"] = new OpenApiDouble(999.99)
                    },
                    new OpenApiObject
                    {
                        ["sku"] = new OpenApiString("SKU-WIRELESS-MOUSE"),
                        ["quantity"] = new OpenApiInteger(2),
                        ["unitPrice"] = new OpenApiDouble(25.50)
                    }
                }
            };
        }
        else if (context.Type == typeof(OrderItemRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["sku"] = new OpenApiString("SKU-LAPTOP-PRO"),
                ["quantity"] = new OpenApiInteger(1),
                ["unitPrice"] = new OpenApiDouble(999.99)
            };
        }
        else if (context.Type == typeof(PaymentRequest))
        {
            schema.Example = new OpenApiObject
            {
                ["orderId"] = new OpenApiString("a1b2c3d4-e5f6-7890-abcd-ef1234567890"),
                ["customerId"] = new OpenApiString("cust-demo-101"),
                ["amount"] = new OpenApiDouble(1050.99),
                ["currency"] = new OpenApiString("USD"),
                ["idempotencyKey"] = new OpenApiString("key-demo-order-101")
            };
        }
    }
}

public class SwaggerExampleOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.Parameters == null) return;

        foreach (var parameter in operation.Parameters)
        {
            if (parameter.Name.Equals("customerId", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Example = new OpenApiString("cust-demo-101");
            }
            else if (parameter.Name.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Example = new OpenApiInteger(1);
            }
            else if (parameter.Name.Equals("pageSize", StringComparison.OrdinalIgnoreCase))
            {
                parameter.Example = new OpenApiInteger(10);
            }
        }
    }
}
