# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["yansenshop.csproj", "./"]
RUN dotnet restore "yansenshop.csproj"

# Copy everything else and build
COPY . .
RUN dotnet publish "yansenshop.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# THIS IS THE NEW LINE: Copies your PrivatePages folder to the server!
COPY --from=build /src/PrivatePages /app/PrivatePages

# Render requires the app to listen on port 8080 by default
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run the compiled app
ENTRYPOINT ["dotnet", "yansenshop.dll"]
