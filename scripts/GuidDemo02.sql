USE [StackOverflow2013]
GO

/****** Object:  Table [dbo].[GuidDemo01]    Script Date: 24/06/2026 16:53:14 ******/
SET ANSI_NULLS ON
GO

SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[GuidDemo02](
	[SomeGuid] [uniqueidentifier] NOT NULL,
 CONSTRAINT [PK_GuidDemo02] PRIMARY KEY CLUSTERED 
(
	[SomeGuid] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) ON [PRIMARY]
) ON [PRIMARY]
GO

ALTER TABLE [dbo].[GuidDemo01] ADD  DEFAULT (newid()) FOR [SomeGuid]
GO



insert into [dbo].[GuidDemo02]
default values 
go 30000