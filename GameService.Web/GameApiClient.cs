using System.Net.Http.Json;

namespace GameService.Web;

public class GameApiClient(HttpClient httpClient)
{
    public async Task<UserResponse[]> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        // In a real app, you'd handle auth headers here
        var users = await httpClient.GetFromJsonAsync<UserResponse[]>("/admin/users", cancellationToken);
        return users ?? [];
    }

    public async Task DeleteUserAsync(int id, CancellationToken cancellationToken = default)
    {
        await httpClient.DeleteAsync($"/admin/users/{id}", cancellationToken);
    }
}

// Reuse the DTO locally or reference a shared library
public record UserResponse(int Id, string Username, string Email);