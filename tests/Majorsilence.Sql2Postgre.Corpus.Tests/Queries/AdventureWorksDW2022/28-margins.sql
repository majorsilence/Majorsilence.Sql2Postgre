SELECT ProductKey, StandardCost, ListPrice, ListPrice - StandardCost AS margin FROM dbo.DimProduct WHERE ListPrice IS NOT NULL
