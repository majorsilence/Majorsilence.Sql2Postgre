SELECT HouseOwnerFlag, NumberCarsOwned, COUNT(*) AS n FROM dbo.DimCustomer GROUP BY HouseOwnerFlag, NumberCarsOwned
