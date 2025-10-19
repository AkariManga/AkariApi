namespace AkariApi.Services;
public class SupabaseService
{
    private readonly Supabase.Client _client;

    public SupabaseService(Supabase.Client client)
    {
        _client = client;
    }

    public Supabase.Client Client => _client;

    public async Task InitializeAsync()
    {
        await _client.InitializeAsync();
    }
}